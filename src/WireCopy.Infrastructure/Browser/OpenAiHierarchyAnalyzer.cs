// Licensed under the MIT License. See LICENSE in the repository root.

using System.ClientModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI.Chat;
using WireCopy.Application.Interfaces;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Configuration;

namespace WireCopy.Infrastructure.Browser;

/// <summary>
/// Hierarchy / curated analyzer backed by OpenAI's Chat Completions API
/// (model defaults to <c>gpt-5-mini</c>). Replaces the previous Anthropic
/// implementation so the user only needs the single OpenAI key already
/// supplied for TTS.
///
/// <para>
/// Uses strict JSON-Schema response formatting where the SDK supports it
/// — eliminates the markdown-fence-stripping and JSON-retry loop the
/// Anthropic version relied on. We still keep a one-shot retry on
/// <see cref="JsonException"/> as a defensive net (the model can refuse,
/// emit a refusal stub, etc.).
/// </para>
/// </summary>
internal sealed class OpenAiHierarchyAnalyzer : IHierarchyAnalyzer
{
    private static readonly JsonSerializerOptions ParseOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly BinaryData PageHierarchySchema = BinaryData.FromString(
        """
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "sections": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "properties": {
                  "name": { "type": "string" },
                  "linkIndices": { "type": "array", "items": { "type": "integer" } },
                  "parentSelectors": { "type": "array", "items": { "type": "string" } },
                  "urlPatterns": { "type": "array", "items": { "type": "string" } },
                  "startCollapsed": { "type": "boolean" }
                },
                "required": ["name", "linkIndices", "parentSelectors", "urlPatterns", "startCollapsed"]
              }
            }
          },
          "required": ["sections"]
        }
        """);

    private static readonly BinaryData CuratedSchema = BinaryData.FromString(
        """
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "excluded": { "type": "array", "items": { "type": "integer" } },
            "stories": { "type": "array", "items": { "type": "integer" } },
            "sections": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "properties": {
                  "name": { "type": "string" },
                  "story_indices": { "type": "array", "items": { "type": "integer" } },
                  "start_collapsed": { "type": "boolean" }
                },
                "required": ["name", "story_indices", "start_collapsed"]
              }
            }
          },
          "required": ["excluded", "stories", "sections"]
        }
        """);

    private readonly OpenAiHierarchyConfiguration _config;
    private readonly OpenAiTtsConfiguration _ttsConfig;
    private readonly IUserSettingsStore _settingsStore;
    private readonly ILogger<OpenAiHierarchyAnalyzer> _logger;
    private readonly ChatCompleter _chatCompleter;

    public OpenAiHierarchyAnalyzer(
        IOptions<OpenAiHierarchyConfiguration> config,
        IOptions<OpenAiTtsConfiguration> ttsConfig,
        IUserSettingsStore settingsStore,
        ILogger<OpenAiHierarchyAnalyzer> logger)
        : this(config, ttsConfig, settingsStore, logger, chatCompleter: null)
    {
    }

    /// <summary>
    /// Test seam: lets unit tests inject a fake completion delegate so the
    /// analyzer's prompt-building / response-parsing code can be exercised
    /// without an actual OpenAI round-trip.
    /// </summary>
    internal OpenAiHierarchyAnalyzer(
        IOptions<OpenAiHierarchyConfiguration> config,
        IOptions<OpenAiTtsConfiguration> ttsConfig,
        IUserSettingsStore settingsStore,
        ILogger<OpenAiHierarchyAnalyzer> logger,
        ChatCompleter? chatCompleter)
    {
        _config = config.Value;
        _ttsConfig = ttsConfig.Value;
        _settingsStore = settingsStore;
        _logger = logger;
        _chatCompleter = chatCompleter ?? DefaultChatCompleterAsync;
    }

    /// <summary>
    /// Delegate signature for the chat-completion call. Returns the assistant
    /// message text. Tests substitute their own implementation.
    /// </summary>
    internal delegate Task<string> ChatCompleter(
        string apiKey,
        string model,
        IEnumerable<ChatMessage> messages,
        ChatCompletionOptions options,
        CancellationToken cancellationToken);

    /// <inheritdoc />
    public bool IsConfigured => !string.IsNullOrWhiteSpace(GetApiKey());

    /// <inheritdoc />
    public async Task<SiteHierarchyConfig> AnalyzePageHierarchyAsync(
        byte[] screenshot,
        List<LinkInfo> links,
        string pageUrl,
        string? promptSuffix = null,
        CancellationToken cancellationToken = default)
    {
        var apiKey = GetApiKey()
            ?? throw new InvalidOperationException(
                "OpenAI API key not configured. Use Setup (press S) to configure.");

        var domain = new Uri(pageUrl).Host.ToLowerInvariant();
        var systemPrompt = BuildHierarchySystemPrompt();
        var userPrompt = BuildHierarchyUserPrompt(links, pageUrl);
        if (!string.IsNullOrEmpty(promptSuffix))
        {
            userPrompt += "\n\n" + promptSuffix;
        }

        _logger.LogInformation(
            "Analyzing page hierarchy for {Url} ({LinkCount} links, {ScreenshotSize} bytes) via {Model}",
            pageUrl,
            links.Count,
            screenshot.Length,
            _config.Model);

        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var messages = BuildMessages(systemPrompt, userPrompt, screenshot);
                var options = BuildOptions(
                    schemaName: "site_hierarchy",
                    schema: PageHierarchySchema,
                    schemaDescription: "Visual link hierarchy detected for the page.");

                var responseText = await _chatCompleter(
                    apiKey, _config.Model, messages, options, cancellationToken).ConfigureAwait(false);

                var config = ParseHierarchyResponse(responseText, domain, pageUrl, _config.Model);

                _logger.LogInformation(
                    "AI hierarchy analysis complete: {SectionCount} sections detected for {Domain}",
                    config.Sections.Count,
                    domain);

                return config;
            }
            catch (JsonException ex) when (attempt == 0)
            {
                _logger.LogWarning(
                    ex, "Malformed JSON in AI hierarchy response, retrying with stricter prompt");

                var sb = new StringBuilder(userPrompt);
                sb.AppendLine();
                sb.AppendLine();
                sb.Append("IMPORTANT: Your previous response was not valid JSON. ");
                sb.Append("Please respond with ONLY a JSON object matching the requested schema, no commentary.");
                userPrompt = sb.ToString();
            }
        }

        throw new InvalidOperationException("Failed to parse AI hierarchy response after retries");
    }

    /// <inheritdoc />
    public async Task<AiCuratedResult> AnalyzeCuratedAsync(
        byte[]? screenshot,
        List<LinkInfo> links,
        string pageUrl,
        CancellationToken cancellationToken = default)
    {
        var apiKey = GetApiKey()
            ?? throw new InvalidOperationException(
                "OpenAI API key not configured. Use Setup (press S) to configure.");

        var contentLinks = links
            .Where(l => l.Type == LinkType.Content)
            .ToList();

        var systemPrompt = BuildCuratedSystemPrompt();
        var userPrompt = BuildCuratedUserPrompt(contentLinks, pageUrl);

        _logger.LogInformation(
            "AI curated analysis for {Url} ({Count} content links, screenshot={HasShot}) via {Model}",
            pageUrl,
            contentLinks.Count,
            screenshot is { Length: > 0 },
            _config.Model);

        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var messages = BuildMessages(systemPrompt, userPrompt, screenshot);
                var options = BuildOptions(
                    schemaName: "ai_curated_layout",
                    schema: CuratedSchema,
                    schemaDescription: "Editorial split of links into excluded promos vs ranked stories.");

                var responseText = await _chatCompleter(
                    apiKey, _config.Model, messages, options, cancellationToken).ConfigureAwait(false);

                return ParseCuratedResponse(responseText, contentLinks);
            }
            catch (JsonException ex) when (attempt == 0)
            {
                _logger.LogWarning(ex, "Malformed JSON in AI curated response, retrying");
                var sb = new StringBuilder(userPrompt);
                sb.Append("\n\nIMPORTANT: respond with ONLY the JSON object — no commentary.");
                userPrompt = sb.ToString();
            }
        }

        throw new InvalidOperationException("Failed to parse AI curated response after retries");
    }

    private static string BuildHierarchySystemPrompt()
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are analyzing webpage screenshots for a terminal-based news reader.");
        sb.AppendLine("Group the content links provided in the user message by visual section, ordered by editorial prominence.");
        sb.AppendLine();
        sb.AppendLine("Group links into sections by editorial prominence:");
        sb.AppendLine("- Hero/lead stories at top (most prominent, largest on page)");
        sb.AppendLine("- Main content feed (primary article list)");
        sb.AppendLine("- Secondary sections (opinion, trending, below-fold areas)");
        sb.AppendLine("- Sidebar content last (least prominent)");
        sb.AppendLine();
        sb.AppendLine("Each section should contain ONLY article/story links. Exclude navigation, ads, newsletter signups, and utility links.");
        sb.AppendLine();
        sb.AppendLine("For each section provide:");
        sb.AppendLine("- name: Short descriptive name (e.g., \"Lead Stories\", \"Opinion\", \"Trending\")");
        sb.AppendLine("- linkIndices: Indices of links in this section");
        sb.AppendLine("- parentSelectors: Common CSS parent selectors from the parent field");
        sb.AppendLine("- urlPatterns: URL path patterns shared by links (e.g., \"/opinion/\")");
        sb.AppendLine("- startCollapsed: true for less prominent sections (sidebar, below fold)");
        sb.AppendLine();
        sb.Append("Return a JSON object with a single \"sections\" array conforming to the response schema.");
        return sb.ToString();
    }

    private static string BuildHierarchyUserPrompt(List<LinkInfo> links, string pageUrl)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Page URL: {pageUrl}");
        sb.AppendLine();
        sb.AppendLine("Content links extracted from the page (index, text, URL, CSS parent):");
        sb.AppendLine();

        var contentLinks = links
            .Select((link, index) => (link, index))
            .Where(pair => pair.link.Type == LinkType.Content);

        foreach (var (link, index) in contentLinks)
        {
            sb.AppendLine($"[{index}] \"{link.DisplayText}\" -> {link.Url}");
            if (!string.IsNullOrEmpty(link.ParentSelector))
            {
                sb.AppendLine($"    parent: {link.ParentSelector}");
            }
        }

        return sb.ToString();
    }

    private static string BuildCuratedSystemPrompt()
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are curating a webpage's links for a terminal-based news reader.");
        sb.AppendLine();
        sb.AppendLine("Do TWO things:");
        sb.AppendLine();
        sb.AppendLine("1. EXCLUDE: identify links that are ads, sponsored content, promos,");
        sb.AppendLine("   newsletter sign-ups, login prompts, navigation/utility links, or any");
        sb.AppendLine("   non-editorial entry. Return their numeric indices in `excluded`.");
        sb.AppendLine("   Be aggressive — if it's not a story, it goes here.");
        sb.AppendLine();
        sb.AppendLine("2. RANK: of the REMAINING links (the actual stories), order them by");
        sb.AppendLine("   editorial prominence on the page. Lead/hero items first, then main");
        sb.AppendLine("   feed, then secondary, then sidebar. Return indices in `stories`.");
        sb.AppendLine();
        sb.AppendLine("3. (optional) propose named SECTIONS that group the stories. Return an");
        sb.AppendLine("   empty array when no useful grouping exists.");
        sb.AppendLine();
        sb.AppendLine("Every numeric index MUST be a valid index from the supplied list.");
        sb.Append("`excluded` and `stories` together MUST cover every index exactly once.");
        return sb.ToString();
    }

    private static string BuildCuratedUserPrompt(List<LinkInfo> contentLinks, string pageUrl)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Page URL: {pageUrl}");
        sb.AppendLine();
        sb.AppendLine("Content links extracted from the page (numbered for reference):");
        sb.AppendLine();
        for (int i = 0; i < contentLinks.Count; i++)
        {
            var link = contentLinks[i];
            sb.AppendLine($"[{i}] \"{link.DisplayText}\" -> {link.Url}");
            if (!string.IsNullOrEmpty(link.ParentSelector))
            {
                sb.AppendLine($"    parent: {link.ParentSelector}");
            }
        }

        return sb.ToString();
    }

    private static List<ChatMessage> BuildMessages(string systemPrompt, string userPrompt, byte[]? screenshot)
    {
        var userParts = new List<ChatMessageContentPart>();
        if (screenshot is { Length: > 0 })
        {
            userParts.Add(ChatMessageContentPart.CreateImagePart(
                BinaryData.FromBytes(screenshot),
                "image/png"));
        }

        userParts.Add(ChatMessageContentPart.CreateTextPart(userPrompt));

        return new List<ChatMessage>
        {
            new SystemChatMessage(systemPrompt),
            new UserChatMessage(userParts),
        };
    }

    private ChatCompletionOptions BuildOptions(string schemaName, BinaryData schema, string schemaDescription)
    {
        var options = new ChatCompletionOptions
        {
            ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                jsonSchemaFormatName: schemaName,
                jsonSchema: schema,
                jsonSchemaFormatDescription: schemaDescription,
                jsonSchemaIsStrict: true),
            MaxOutputTokenCount = _config.MaxTokens,
        };

        // ReasoningEffortLevel is gpt-5 / o-series only; the SDK's "Minimal"
        // member is marked experimental but is exactly the right knob for
        // classification work. Anything else falls back to no override.
#pragma warning disable OPENAI001 // Experimental API
        if (TryMapReasoningEffort(_config.ReasoningEffort, out var level))
        {
            options.ReasoningEffortLevel = level;
        }
#pragma warning restore OPENAI001

        return options;
    }

#pragma warning disable OPENAI001 // Experimental API surface for reasoning levels
#pragma warning disable SA1204 // grouped with the instance method that calls it
    private static bool TryMapReasoningEffort(string? value, out ChatReasoningEffortLevel level)
    {
        level = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case "minimal":
                level = ChatReasoningEffortLevel.Minimal;
                return true;
            case "low":
                level = ChatReasoningEffortLevel.Low;
                return true;
            case "medium":
                level = ChatReasoningEffortLevel.Medium;
                return true;
            case "high":
                level = ChatReasoningEffortLevel.High;
                return true;
            default:
                return false;
        }
    }
#pragma warning restore SA1204
#pragma warning restore OPENAI001

    private static async Task<string> DefaultChatCompleterAsync(
        string apiKey,
        string model,
        IEnumerable<ChatMessage> messages,
        ChatCompletionOptions options,
        CancellationToken cancellationToken)
    {
        var client = new ChatClient(model, new ApiKeyCredential(apiKey));
        var result = await client.CompleteChatAsync(messages, options, cancellationToken).ConfigureAwait(false);
        var completion = result.Value;

        if (!string.IsNullOrEmpty(completion.Refusal))
        {
            throw new InvalidOperationException(
                $"OpenAI refused the request: {completion.Refusal}");
        }

        var sb = new StringBuilder();
        foreach (var part in completion.Content)
        {
            if (part.Kind == ChatMessageContentPartKind.Text)
            {
                sb.Append(part.Text);
            }
        }

        return sb.ToString();
    }

    private string? GetApiKey()
    {
        var storedKey = _settingsStore.Get("OpenAiApiKey");
        if (!string.IsNullOrWhiteSpace(storedKey))
        {
            return storedKey;
        }

        if (!string.IsNullOrWhiteSpace(_ttsConfig.ApiKey))
        {
            return _ttsConfig.ApiKey;
        }

        var envKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        return string.IsNullOrWhiteSpace(envKey) ? null : envKey;
    }

#pragma warning disable SA1202 // internal test seam grouped with the production code that produces it
    /// <summary>
    /// Parses the strict-schema hierarchy response into the domain
    /// <see cref="SiteHierarchyConfig"/>. Exposed internally so unit tests
    /// can drive the parser without round-tripping through OpenAI.
    /// </summary>
    internal static SiteHierarchyConfig ParseHierarchyResponse(
        string responseText, string domain, string pageUrl, string modelVersion)
    {
        var jsonText = StripOptionalFences(responseText);

        // Two shapes are tolerated: the strict-schema {"sections":[...]}
        // wrapper, and the legacy bare array form (kept so the retry path
        // still parses if the model decides to drop the wrapper).
        List<AiSectionResponse>? sections = null;
        var trimmed = jsonText.TrimStart();
        if (trimmed.StartsWith('{'))
        {
            var wrapper = JsonSerializer.Deserialize<AiHierarchyEnvelope>(jsonText, ParseOptions);
            sections = wrapper?.Sections;
        }
        else if (trimmed.StartsWith('['))
        {
            sections = JsonSerializer.Deserialize<List<AiSectionResponse>>(jsonText, ParseOptions);
        }

        if (sections is null)
        {
            throw new JsonException("Failed to deserialize AI hierarchy response");
        }

        var hierarchySections = sections
            .Select((s, i) => new HierarchySection
            {
                Name = s.Name ?? $"Section {i + 1}",
                SortOrder = i,
                ParentSelectors = s.ParentSelectors ?? new List<string>(),
                UrlPatterns = s.UrlPatterns ?? new List<string>(),
                StartCollapsed = s.StartCollapsed,
            })
            .ToList();

        var uri = new Uri(pageUrl);
        var escapedDomain = Regex.Escape(uri.Host);
        var pathPattern = uri.AbsolutePath == "/"
            ? "/?"
            : Regex.Escape(uri.AbsolutePath);
        var urlPattern = $"^https?://(www\\.)?{escapedDomain}{pathPattern}";

        return new SiteHierarchyConfig
        {
            Domain = domain,
            UrlPattern = urlPattern,
            Sections = hierarchySections,
            CreatedAt = DateTime.UtcNow,
            ModelVersion = modelVersion,
        };
    }

    /// <summary>
    /// Parses the strict-schema curated response into an
    /// <see cref="AiCuratedResult"/>. Exposed internally for unit tests.
    /// </summary>
    internal static AiCuratedResult ParseCuratedResponse(
        string responseText, List<LinkInfo> contentLinks)
    {
        var jsonText = StripOptionalFences(responseText);

        var parsed = JsonSerializer.Deserialize<AiCuratedResponse>(jsonText, ParseOptions)
            ?? throw new JsonException("Failed to deserialize AI curated response");

        string KeyAt(int idx) =>
            idx >= 0 && idx < contentLinks.Count
                ? AiCuratedResult.KeyFor(contentLinks[idx].Url)
                : string.Empty;

        var excludedKeys = (parsed.Excluded ?? new List<int>())
            .Select(KeyAt)
            .Where(k => k.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var storyKeys = (parsed.Stories ?? new List<int>())
            .Select(KeyAt)
            .Where(k => k.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var sectionsOut = new List<AiCuratedSection>();
        if (parsed.Sections != null)
        {
            foreach (var s in parsed.Sections)
            {
                var sectionKeys = (s.StoryIndices ?? new List<int>())
                    .Select(KeyAt)
                    .Where(k => k.Length > 0)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                if (sectionKeys.Count == 0)
                {
                    continue;
                }

                sectionsOut.Add(new AiCuratedSection
                {
                    Name = string.IsNullOrWhiteSpace(s.Name) ? "Stories" : s.Name!,
                    StoryLinkKeys = sectionKeys,
                    StartCollapsed = s.StartCollapsed,
                });
            }
        }

        return new AiCuratedResult
        {
            ExcludedLinkKeys = excludedKeys,
            StoryOrderLinkKeys = storyKeys,
            Sections = sectionsOut,
            AnalyzedAt = DateTime.UtcNow,
        };
    }
#pragma warning restore SA1202

    /// <summary>
    /// Strict JSON-Schema responses should never include markdown fences,
    /// but the legacy retry path could produce them. Strip defensively so a
    /// single accidental fence doesn't poison the parse.
    /// </summary>
    private static string StripOptionalFences(string responseText)
    {
        var jsonText = responseText.Trim();
        if (jsonText.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = jsonText.IndexOf('\n');
            if (firstNewline >= 0)
            {
                jsonText = jsonText[(firstNewline + 1)..];
            }

            if (jsonText.EndsWith("```", StringComparison.Ordinal))
            {
                jsonText = jsonText[..^3].TrimEnd();
            }
        }

        return jsonText;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "SonarAnalyzer.CSharp",
        "S3459:Unassigned members should be removed",
        Justification = "Properties are assigned by JSON deserialization")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "SonarAnalyzer.CSharp",
        "S1144:Unused private types or members should be removed",
        Justification = "Properties read by deserialization")]
    private sealed class AiHierarchyEnvelope
    {
        [JsonPropertyName("sections")]
        public List<AiSectionResponse>? Sections { get; set; }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "SonarAnalyzer.CSharp",
        "S3459:Unassigned members should be removed",
        Justification = "Properties are assigned by JSON deserialization")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "SonarAnalyzer.CSharp",
        "S1144:Unused private types or members should be removed",
        Justification = "Properties are assigned/read by JSON deserialization and LINQ projection")]
    private sealed class AiSectionResponse
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("linkIndices")]
        public List<int>? LinkIndices { get; set; }

        [JsonPropertyName("parentSelectors")]
        public List<string>? ParentSelectors { get; set; }

        [JsonPropertyName("urlPatterns")]
        public List<string>? UrlPatterns { get; set; }

        [JsonPropertyName("startCollapsed")]
        public bool StartCollapsed { get; set; }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "SonarAnalyzer.CSharp",
        "S3459:Unassigned members should be removed",
        Justification = "Properties are assigned by JSON deserialization")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "SonarAnalyzer.CSharp",
        "S1144:Unused private types or members should be removed",
        Justification = "Properties read by deserialization")]
    private sealed class AiCuratedResponse
    {
        [JsonPropertyName("excluded")]
        public List<int>? Excluded { get; set; }

        [JsonPropertyName("stories")]
        public List<int>? Stories { get; set; }

        [JsonPropertyName("sections")]
        public List<AiCuratedSectionResponse>? Sections { get; set; }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "SonarAnalyzer.CSharp",
        "S3459:Unassigned members should be removed",
        Justification = "Properties are assigned by JSON deserialization")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "SonarAnalyzer.CSharp",
        "S1144:Unused private types or members should be removed",
        Justification = "Properties read by deserialization")]
    private sealed class AiCuratedSectionResponse
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("story_indices")]
        public List<int>? StoryIndices { get; set; }

        [JsonPropertyName("start_collapsed")]
        public bool StartCollapsed { get; set; }
    }
}
