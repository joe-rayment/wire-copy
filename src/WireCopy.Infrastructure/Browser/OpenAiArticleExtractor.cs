// Licensed under the MIT License. See LICENSE in the repository root.

using System.ClientModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI.Chat;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Infrastructure.Configuration;

namespace WireCopy.Infrastructure.Browser;

/// <summary>
/// Article-extraction analyzer backed by OpenAI's Chat Completions API.
/// Mirrors <see cref="OpenAiHierarchyAnalyzer"/>'s pattern: strict JSON-Schema
/// response format, one-shot retry on malformed JSON, shared API-key
/// resolution chain (settings store → TTS config → env var).
///
/// <para>
/// The candidate config returned here is a *single* <see cref="PageTypeEntry"/>
/// wrapped in an <see cref="ArticleSelectorConfig"/>; the caller is responsible
/// for the self-test gate (running <see cref="SelectorBasedArticleExtractor"/>
/// against the candidate and validating the result with
/// <c>ReadableContentExtractor.ValidateContentQuality</c>) before persisting.
/// </para>
/// </summary>
internal sealed class OpenAiArticleExtractor : IAiArticleExtractor
{
    private static readonly JsonSerializerOptions ParseOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly BinaryData ArticleSelectorSchema = BinaryData.FromString(
        """
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "name": { "type": "string" },
            "pageType": { "type": "string" },
            "priority": { "type": "integer" },
            "matcher": {
              "type": "object",
              "additionalProperties": false,
              "properties": {
                "urlPattern": { "type": ["string", "null"] },
                "ldJsonType": { "type": ["string", "null"] },
                "bodyClassContains": { "type": ["string", "null"] }
              },
              "required": ["urlPattern", "ldJsonType", "bodyClassContains"]
            },
            "selectors": {
              "type": "object",
              "additionalProperties": false,
              "properties": {
                "headline": { "type": "array", "items": { "type": "string" } },
                "byline": { "type": "array", "items": { "type": "string" } },
                "publishDate": { "type": "array", "items": { "type": "string" } },
                "body": { "type": "array", "items": { "type": "string" } },
                "excludeRegions": { "type": "array", "items": { "type": "string" } }
              },
              "required": ["headline", "byline", "publishDate", "body", "excludeRegions"]
            },
            "quality": {
              "type": "object",
              "additionalProperties": false,
              "properties": {
                "minWords": { "type": "integer" },
                "minParagraphs": { "type": "integer" }
              },
              "required": ["minWords", "minParagraphs"]
            }
          },
          "required": ["name", "pageType", "priority", "matcher", "selectors", "quality"]
        }
        """);

    private readonly OpenAiHierarchyConfiguration _config;
    private readonly OpenAiTtsConfiguration _ttsConfig;
    private readonly IUserSettingsStore _settingsStore;
    private readonly ILogger<OpenAiArticleExtractor> _logger;
    private readonly ChatCompleter _chatCompleter;

    public OpenAiArticleExtractor(
        IOptions<OpenAiHierarchyConfiguration> config,
        IOptions<OpenAiTtsConfiguration> ttsConfig,
        IUserSettingsStore settingsStore,
        ILogger<OpenAiArticleExtractor> logger)
        : this(config, ttsConfig, settingsStore, logger, chatCompleter: null)
    {
    }

    /// <summary>
    /// Test seam for stubbing the chat completion call.
    /// </summary>
    internal OpenAiArticleExtractor(
        IOptions<OpenAiHierarchyConfiguration> config,
        IOptions<OpenAiTtsConfiguration> ttsConfig,
        IUserSettingsStore settingsStore,
        ILogger<OpenAiArticleExtractor> logger,
        ChatCompleter? chatCompleter)
    {
        _config = config.Value;
        _ttsConfig = ttsConfig.Value;
        _settingsStore = settingsStore;
        _logger = logger;
        _chatCompleter = chatCompleter ?? DefaultChatCompleterAsync;
    }

    internal delegate Task<string> ChatCompleter(
        string apiKey,
        string model,
        IEnumerable<ChatMessage> messages,
        ChatCompletionOptions options,
        CancellationToken cancellationToken);

    /// <inheritdoc />
    public bool IsConfigured => !string.IsNullOrWhiteSpace(GetApiKey());

    /// <inheritdoc />
    public async Task<ArticleSelectorConfig?> AnalyzeAsync(string url, string html, CancellationToken ct)
    {
        var apiKey = GetApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogDebug("OpenAI API key not configured; skipping AI article extraction");
            return null;
        }

        var domain = ExtractDomain(url);
        if (domain == null)
        {
            _logger.LogDebug("Could not extract domain from {Url}; skipping AI article extraction", url);
            return null;
        }

        var systemPrompt = BuildSystemPrompt();
        var trimmedHtml = TrimHtmlForPrompt(html);
        var userPromptBuilder = new StringBuilder(BuildUserPrompt(url, trimmedHtml));

        for (int attempt = 0; attempt < 2; attempt++)
        {
            var userPrompt = userPromptBuilder.ToString();
            try
            {
                var messages = new List<ChatMessage>
                {
                    new SystemChatMessage(systemPrompt),
                    new UserChatMessage(userPrompt),
                };

                var options = new ChatCompletionOptions
                {
                    ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                        jsonSchemaFormatName: "article_selectors",
                        jsonSchema: ArticleSelectorSchema,
                        jsonSchemaFormatDescription: "Selectors used to extract article content from the page.",
                        jsonSchemaIsStrict: true),
                    MaxOutputTokenCount = _config.MaxTokens,
                };

                var responseText = await _chatCompleter(
                    apiKey,
                    _config.Model,
                    messages,
                    options,
                    ct).ConfigureAwait(false);

                return ParseResponse(responseText, domain, url, _config.Model);
            }
            catch (JsonException ex) when (attempt == 0)
            {
                _logger.LogWarning(ex, "Malformed JSON in AI article-extractor response, retrying");
                userPromptBuilder.Append("\n\nIMPORTANT: respond with ONLY a JSON object matching the requested schema, no commentary.");
            }
            catch (Exception ex) when (attempt == 1 || ex is not JsonException)
            {
                _logger.LogWarning(ex, "AI article extraction failed for {Url}", url);
                return null;
            }
        }

        return null;
    }

    private static string BuildSystemPrompt()
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are analyzing an HTML page to discover the CSS / XPath selectors that extract its article content.");
        sb.AppendLine();
        sb.AppendLine("For the supplied page, return a JSON object describing:");
        sb.AppendLine("- name: short identifier for this page type (e.g. \"article\", \"live-blog\", \"recipe\")");
        sb.AppendLine("- pageType: one of \"Article\", \"LiveBlog\", \"Recipe\", \"Opinion\", \"SectionFront\"");
        sb.AppendLine("- priority: integer; higher means more specific. Generic articles ~10, live-blogs / recipes ~100");
        sb.AppendLine("- matcher: signals that identify this kind of page on this domain. Populate as many as are reliable:");
        sb.AppendLine("    urlPattern (regex), ldJsonType (e.g. NewsArticle / LiveBlogPosting), bodyClassContains (substring)");
        sb.AppendLine("- selectors: ordered XPath arrays for each field. Use //tag[predicate] form. Provide multiple selectors per field for robustness.");
        sb.AppendLine("    headline: heading containing the article title");
        sb.AppendLine("    byline: author name container");
        sb.AppendLine("    publishDate: element with the publication timestamp (datetime attribute preferred)");
        sb.AppendLine("    body: container(s) holding the article paragraphs");
        sb.AppendLine("    excludeRegions: ads, related-content widgets, comments, newsletter signups inside the body");
        sb.AppendLine("- quality: minimum bar for the result (minWords, minParagraphs)");
        sb.AppendLine();
        sb.Append("Output JSON ONLY. No commentary.");
        return sb.ToString();
    }

    private static string BuildUserPrompt(string url, string trimmedHtml)
    {
        var sb = new StringBuilder();
        sb.Append("Page URL: ").AppendLine(url);
        sb.AppendLine();
        sb.AppendLine("Trimmed HTML (head + body sample):");
        sb.AppendLine(trimmedHtml);
        return sb.ToString();
    }

    /// <summary>
    /// HTML can be megabytes; we cap at ~80 KB to stay well under the token
    /// budget. We keep the head element intact (matchers depend on meta /
    /// ld+json) and a generous prefix of the body.
    /// </summary>
    private static string TrimHtmlForPrompt(string html)
    {
        const int MaxLength = 80_000;
        if (string.IsNullOrEmpty(html) || html.Length <= MaxLength)
        {
            return html ?? string.Empty;
        }

        return html[..MaxLength] + "\n<!-- truncated -->";
    }

    private static ArticleSelectorConfig ParseResponse(string responseText, string domain, string sampleUrl, string model)
    {
        var jsonText = StripOptionalFences(responseText);

        var raw = JsonSerializer.Deserialize<RawEntry>(jsonText, ParseOptions)
            ?? throw new JsonException("Failed to deserialize AI article-extractor response");

        if (!Enum.TryParse<PageType>(raw.PageType, ignoreCase: true, out var pageType))
        {
            pageType = PageType.Article;
        }

        var entry = new PageTypeEntry
        {
            Name = string.IsNullOrWhiteSpace(raw.Name) ? "article" : raw.Name!,
            PageType = pageType,
            Priority = raw.Priority,
            Matcher = new PageTypeMatcher
            {
                UrlPattern = raw.Matcher?.UrlPattern,
                LdJsonType = raw.Matcher?.LdJsonType,
                BodyClassContains = raw.Matcher?.BodyClassContains,
            },
            Selectors = new ArticleSelectors
            {
                Headline = raw.Selectors?.Headline ?? new List<string>(),
                Byline = raw.Selectors?.Byline ?? new List<string>(),
                PublishDate = raw.Selectors?.PublishDate ?? new List<string>(),
                Body = raw.Selectors?.Body ?? new List<string>(),
                ExcludeRegions = raw.Selectors?.ExcludeRegions ?? new List<string>(),
            },
            Quality = new ArticleQualityThresholds
            {
                MinWords = raw.Quality?.MinWords ?? 100,
                MinParagraphs = raw.Quality?.MinParagraphs ?? 3,
            },
            Provenance = new ProvenanceInfo
            {
                Model = model,
                GeneratedAt = DateTime.UtcNow,
                SampleUrl = sampleUrl,
                ConsecutiveFailures = 0,
            },
        };

        return new ArticleSelectorConfig
        {
            SchemaVersion = ArticleLayoutStore.CurrentSchemaVersion,
            Domain = domain,
            UpdatedAt = DateTime.UtcNow,
            PageTypes = new List<PageTypeEntry> { entry },
        };
    }

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

    private static string? ExtractDomain(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        try
        {
            var uri = new Uri(url);
            return uri.Host.ToLowerInvariant();
        }
        catch
        {
            return null;
        }
    }

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

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "SonarAnalyzer.CSharp",
        "S3459:Unassigned members should be removed",
        Justification = "Properties are assigned by JSON deserialization")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "SonarAnalyzer.CSharp",
        "S1144:Unused private types or members should be removed",
        Justification = "Properties read by deserialization")]
    private sealed class RawEntry
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("pageType")]
        public string? PageType { get; set; }

        [JsonPropertyName("priority")]
        public int Priority { get; set; }

        [JsonPropertyName("matcher")]
        public RawMatcher? Matcher { get; set; }

        [JsonPropertyName("selectors")]
        public RawSelectors? Selectors { get; set; }

        [JsonPropertyName("quality")]
        public RawQuality? Quality { get; set; }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "SonarAnalyzer.CSharp",
        "S3459:Unassigned members should be removed",
        Justification = "Properties are assigned by JSON deserialization")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "SonarAnalyzer.CSharp",
        "S1144:Unused private types or members should be removed",
        Justification = "Properties read by deserialization")]
    private sealed class RawMatcher
    {
        [JsonPropertyName("urlPattern")]
        public string? UrlPattern { get; set; }

        [JsonPropertyName("ldJsonType")]
        public string? LdJsonType { get; set; }

        [JsonPropertyName("bodyClassContains")]
        public string? BodyClassContains { get; set; }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "SonarAnalyzer.CSharp",
        "S3459:Unassigned members should be removed",
        Justification = "Properties are assigned by JSON deserialization")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "SonarAnalyzer.CSharp",
        "S1144:Unused private types or members should be removed",
        Justification = "Properties read by deserialization")]
    private sealed class RawSelectors
    {
        [JsonPropertyName("headline")]
        public List<string>? Headline { get; set; }

        [JsonPropertyName("byline")]
        public List<string>? Byline { get; set; }

        [JsonPropertyName("publishDate")]
        public List<string>? PublishDate { get; set; }

        [JsonPropertyName("body")]
        public List<string>? Body { get; set; }

        [JsonPropertyName("excludeRegions")]
        public List<string>? ExcludeRegions { get; set; }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "SonarAnalyzer.CSharp",
        "S3459:Unassigned members should be removed",
        Justification = "Properties are assigned by JSON deserialization")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "SonarAnalyzer.CSharp",
        "S1144:Unused private types or members should be removed",
        Justification = "Properties read by deserialization")]
    private sealed class RawQuality
    {
        [JsonPropertyName("minWords")]
        public int? MinWords { get; set; }

        [JsonPropertyName("minParagraphs")]
        public int? MinParagraphs { get; set; }
    }
}
