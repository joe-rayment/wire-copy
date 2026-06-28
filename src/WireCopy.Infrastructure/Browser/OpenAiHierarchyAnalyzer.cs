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
    /// <summary>Clamp: options offered per question (keeps the modal + token cost bounded).</summary>
    internal const int MaxOptionsPerQuestion = 6;

    /// <summary>Clamp: example link indices carried per option.</summary>
    internal const int MaxExampleIndicesPerOption = 5;

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

    // workspace-frpl.10 (B9b): single-label section classification for run-time recovery.
    private static readonly BinaryData SectionClassificationSchema = BinaryData.FromString(
        """
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "label": { "type": ["string", "null"] },
            "confidence": { "type": "number" }
          },
          "required": ["label", "confidence"]
        }
        """);

    private static readonly BinaryData SetupQuestionsSchema = BinaryData.FromString(
        """
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "proposed_pattern": {
              "type": "object",
              "additionalProperties": false,
              "properties": {
                "top_story": { "$ref": "#/$defs/option" },
                "tiers": { "type": "array", "items": { "$ref": "#/$defs/option" } },
                "exclude": { "type": "array", "items": { "$ref": "#/$defs/option" } }
              },
              "required": ["top_story", "tiers", "exclude"]
            },
            "questions": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "properties": {
                  "id": { "type": "string" },
                  "prompt": { "type": "string" },
                  "kind": { "type": "string", "enum": ["pick_main", "confirm_exclude", "confirm_order", "group_by"] },
                  "default_answer": { "type": "string" },
                  "options": { "type": "array", "items": { "$ref": "#/$defs/option" } }
                },
                "required": ["id", "prompt", "kind", "default_answer", "options"]
              }
            }
          },
          "required": ["proposed_pattern", "questions"],
          "$defs": {
            "option": {
              "type": "object",
              "additionalProperties": false,
              "properties": {
                "label": { "type": "string" },
                "parent_selector": { "type": "string" },
                "url_pattern": { "type": "string" },
                "example_link_indices": { "type": "array", "items": { "type": "integer" } }
              },
              "required": ["label", "parent_selector", "url_pattern", "example_link_indices"]
            }
          }
        }
        """);

    private static readonly BinaryData SetupPatternSchema = BinaryData.FromString(
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
                  "parent_selectors": { "type": "array", "items": { "type": "string" } },
                  "url_patterns": { "type": "array", "items": { "type": "string" } },
                  "story_indices": { "type": "array", "items": { "type": "integer" } },
                  "start_collapsed": { "type": "boolean" }
                },
                "required": ["name", "parent_selectors", "url_patterns", "story_indices", "start_collapsed"]
              }
            },
            "exclude_selectors": { "type": "array", "items": { "type": "string" } },
            "exclude_url_patterns": { "type": "array", "items": { "type": "string" } },
            "exclude_indices": { "type": "array", "items": { "type": "integer" } },
            "confidence": { "type": "number" },
            "confirm_question": {
              "anyOf": [
                { "type": "null" },
                {
                  "type": "object",
                  "additionalProperties": false,
                  "properties": {
                    "prompt": { "type": "string" },
                    "options": {
                      "type": "array",
                      "items": {
                        "type": "object",
                        "additionalProperties": false,
                        "properties": {
                          "label": { "type": "string" },
                          "parent_selector": { "type": "string" },
                          "url_pattern": { "type": "string" }
                        },
                        "required": ["label", "parent_selector", "url_pattern"]
                      }
                    }
                  },
                  "required": ["prompt", "options"]
                }
              ]
            }
          },
          "required": ["sections", "exclude_selectors", "exclude_url_patterns", "exclude_indices", "confidence", "confirm_question"]
        }
        """);

    // workspace-q77e: model section names that mark a non-story rail (sponsor /
    // advertiser / promo / podcast / audio / event-calendar). These are excluded
    // wholesale, never rendered as a (even collapsed) section. Whole-word so a
    // real "Sports" or editorial section is not swept up.
    private static readonly Regex NonStoryRailName = new(
        @"\b(sponsor(ed|s)?|advertis\w*|promo(s|tion(al)?)?|podcasts?|audio|calendar)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

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
                "OpenAI API key not configured. Open Setup from the launcher (press c) to configure.");

        var domain = HierarchyDomainKey.FromUrl(pageUrl);
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
        string? userGuidance = null,
        string? reasoningEffortOverride = null,
        CancellationToken cancellationToken = default)
    {
        var apiKey = GetApiKey()
            ?? throw new InvalidOperationException(
                "OpenAI API key not configured. Open Setup from the launcher (press c) to configure.");

        var contentLinks = links
            .Where(l => l.Type == LinkType.Content)
            .ToList();

        var systemPrompt = BuildCuratedSystemPrompt(screenshot is { Length: > 0 });
        var userPrompt = BuildCuratedUserPrompt(contentLinks, pageUrl);

        // workspace-99ve: append user-supplied editorial guidance to the
        // user message so the analyzer steers toward the requested outcome
        // ("exclude opinion pieces", "put COVID first", etc.). Empty /
        // whitespace = no guidance, behave as before.
        if (!string.IsNullOrWhiteSpace(userGuidance))
        {
            var sb = new StringBuilder(userPrompt);
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine("USER GUIDANCE — please weight this when ranking and excluding:");
            sb.AppendLine(userGuidance.Trim());
            userPrompt = sb.ToString();
        }

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
                    schemaDescription: "Editorial split of links into excluded promos vs ranked stories.",
                    reasoningEffortOverride: reasoningEffortOverride);

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

    /// <inheritdoc />
    public async Task<SiteSetupProposal> ProposeSetupQuestionsAsync(
        byte[]? screenshot,
        List<LinkInfo> links,
        string pageUrl,
        CancellationToken cancellationToken = default)
    {
        var apiKey = GetApiKey()
            ?? throw new InvalidOperationException(
                "OpenAI API key not configured. Open Setup from the launcher (press c) to configure.");

        var contentLinks = links.Where(l => l.Type == LinkType.Content).ToList();
        var systemPrompt = BuildSetupQuestionsSystemPrompt(_config.MaxSetupQuestions, screenshot is { Length: > 0 });
        var userPrompt = BuildCuratedUserPrompt(contentLinks, pageUrl);

        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var messages = BuildMessages(systemPrompt, userPrompt, screenshot);
                var options = BuildOptions(
                    schemaName: "site_setup_questions",
                    schema: SetupQuestionsSchema,
                    schemaDescription: "A proposed layout pattern plus a bounded set of clarifying questions.",
                    reasoningEffortOverride: _config.SetupReasoningEffort,
                    maxOutputTokensOverride: EstimateSetupOutputTokens(_config.MaxSetupQuestions));

                var responseText = await _chatCompleter(
                    apiKey, _config.Model, messages, options, cancellationToken).ConfigureAwait(false);

                return ParseSetupQuestions(responseText, _config.MaxSetupQuestions);
            }
            catch (JsonException ex) when (attempt == 0)
            {
                _logger.LogWarning(ex, "Malformed JSON in setup-questions response, retrying");
                userPrompt = new StringBuilder(userPrompt)
                    .Append("\n\nIMPORTANT: respond with ONLY the JSON object — no commentary.")
                    .ToString();
            }
        }

        throw new InvalidOperationException("Failed to parse setup-questions response after retries");
    }

    /// <inheritdoc />
    public async Task<InferredPattern> InferPatternFromAnswersAsync(
        byte[]? screenshot,
        List<LinkInfo> links,
        string pageUrl,
        SiteSetupProposal proposal,
        IReadOnlyList<SetupAnswer> answers,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(answers);

        var apiKey = GetApiKey()
            ?? throw new InvalidOperationException(
                "OpenAI API key not configured. Open Setup from the launcher (press c) to configure.");

        var contentLinks = links.Where(l => l.Type == LinkType.Content).ToList();
        var systemPrompt = BuildInferPatternSystemPrompt(screenshot is { Length: > 0 });
        var userPrompt = BuildInferPatternUserPrompt(contentLinks, pageUrl, proposal, answers);

        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var messages = BuildMessages(systemPrompt, userPrompt, screenshot);
                var options = BuildOptions(
                    schemaName: "site_pattern",
                    schema: SetupPatternSchema,
                    schemaDescription: "Durable selector/url-pattern sections + exclusion rules.",
                    reasoningEffortOverride: _config.SetupReasoningEffort);

                var responseText = await _chatCompleter(
                    apiKey, _config.Model, messages, options, cancellationToken).ConfigureAwait(false);

                return ParsePatternFromAnswers(responseText, contentLinks, pageUrl, _config.Model);
            }
            catch (JsonException ex) when (attempt == 0)
            {
                _logger.LogWarning(ex, "Malformed JSON in infer-pattern response, retrying");
                userPrompt = new StringBuilder(userPrompt)
                    .Append("\n\nIMPORTANT: respond with ONLY the JSON object — no commentary.")
                    .ToString();
            }
        }

        throw new InvalidOperationException("Failed to parse infer-pattern response after retries");
    }

    /// <inheritdoc />
    public async Task<InferredPattern> RefineLayoutAsync(
        byte[]? screenshot,
        List<LinkInfo> links,
        string pageUrl,
        SiteHierarchyConfig currentConfig,
        string instruction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(currentConfig);
        ArgumentException.ThrowIfNullOrWhiteSpace(instruction);

        var apiKey = GetApiKey()
            ?? throw new InvalidOperationException(
                "OpenAI API key not configured. Open Setup from the launcher (press c) to configure.");

        var contentLinks = links.Where(l => l.Type == LinkType.Content).ToList();
        var systemPrompt = BuildRefineSystemPrompt(screenshot is { Length: > 0 });
        var userPrompt = BuildRefineUserPrompt(contentLinks, pageUrl, currentConfig, instruction);

        _logger.LogInformation(
            "Refining layout for {Url} ({SectionCount} current sections) via {Model}: {Instruction}",
            pageUrl,
            currentConfig.Sections.Count,
            _config.Model,
            instruction.Length > 80 ? instruction[..80] : instruction);

        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var messages = BuildMessages(systemPrompt, userPrompt, screenshot);
                var options = BuildOptions(
                    schemaName: "site_pattern",
                    schema: SetupPatternSchema,
                    schemaDescription: "The current layout edited by exactly the user's change; everything else identical.",
                    reasoningEffortOverride: _config.SetupReasoningEffort);

                var responseText = await _chatCompleter(
                    apiKey, _config.Model, messages, options, cancellationToken).ConfigureAwait(false);

                return ParsePatternFromAnswers(responseText, contentLinks, pageUrl, _config.Model);
            }
            catch (JsonException ex) when (attempt == 0)
            {
                _logger.LogWarning(ex, "Malformed JSON in refine-layout response, retrying");
                userPrompt = new StringBuilder(userPrompt)
                    .Append("\n\nIMPORTANT: respond with ONLY the JSON object — no commentary.")
                    .ToString();
            }
        }

        throw new InvalidOperationException("Failed to parse refine-layout response after retries");
    }

    /// <inheritdoc />
    public async Task<SectionClassification> ClassifySectionAsync(
        IReadOnlyList<string> candidateLabels,
        string intent,
        CancellationToken cancellationToken = default)
    {
        if (candidateLabels is null || candidateLabels.Count == 0 || string.IsNullOrWhiteSpace(intent))
        {
            return SectionClassification.None;
        }

        var apiKey = GetApiKey()
            ?? throw new InvalidOperationException(
                "OpenAI API key not configured. Open Setup from the launcher (press c) to configure.");

        var systemPrompt =
            "You map a wanted news section to the section headings actually present on a page today. " +
            "Pick the SINGLE present heading that best matches the wanted section's meaning, or null if none does. " +
            "Return only the heading text exactly as given, plus a 0..1 confidence. Be conservative: " +
            "return null (or a low confidence) when the match is ambiguous or weak.";
        var sb = new StringBuilder();
        sb.AppendLine($"Wanted section: {intent.Trim()}");
        sb.AppendLine("Headings present today (choose exactly one or null):");
        foreach (var label in candidateLabels)
        {
            sb.AppendLine($"- {label}");
        }

        var messages = BuildMessages(systemPrompt, sb.ToString(), screenshot: null);
        var options = BuildOptions(
            schemaName: "section_classification",
            schema: SectionClassificationSchema,
            schemaDescription: "The single present heading that best matches the wanted section, with confidence.",
            reasoningEffortOverride: "minimal");

        var responseText = await _chatCompleter(
            apiKey, _config.Model, messages, options, cancellationToken).ConfigureAwait(false);

        return ParseSectionClassification(responseText, candidateLabels);
    }

    /// <summary>
    /// workspace-frpl.10: parses the classify response and CLAMPS it — the label
    /// must be one of the offered candidates (case-insensitive) or it is treated as
    /// "no match" (guards against a hallucinated heading), and confidence is clamped
    /// to [0,1]. The recovery tier then applies its own confidence gate + self-test.
    /// </summary>
    internal static SectionClassification ParseSectionClassification(
        string responseText, IReadOnlyList<string> candidateLabels)
    {
        var jsonText = StripOptionalFences(responseText);
        var parsed = JsonSerializer.Deserialize<SectionClassificationResponse>(jsonText, ParseOptions)
            ?? throw new JsonException("Failed to deserialize section-classification response");

        if (string.IsNullOrWhiteSpace(parsed.Label))
        {
            return SectionClassification.None;
        }

        var match = candidateLabels.FirstOrDefault(
            c => string.Equals(c, parsed.Label, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            return SectionClassification.None; // hallucinated label not in the offered set
        }

        return new SectionClassification
        {
            CandidateLabel = match,
            Confidence = Math.Clamp(parsed.Confidence, 0.0, 1.0),
        };
    }

    /// <summary>
    /// workspace-5oe9.7: a generous output-token budget for the proposal call,
    /// scaled to the question cap. Always at least the configured MaxTokens.
    /// </summary>
    internal static int EstimateSetupOutputTokens(int maxQuestions)
    {
        // proposed_pattern (~400) + maxQuestions * (MaxOptionsPerQuestion options
        // * ~60 tokens each + ~80 framing).
        var perQuestion = (MaxOptionsPerQuestion * 60) + 80;
        return 400 + (Math.Max(1, maxQuestions) * perQuestion);
    }

    /// <summary>
    /// workspace-6yb7.2: majority-external content links mark an aggregator page;
    /// drives the aggregator note in the prompt. Shares the LinkExtractor
    /// promotion thresholds so the two verdicts cannot drift (the population
    /// differs — content links here vs story-shaped links there — but the
    /// count floor and external-share bar are one definition).
    /// </summary>
    internal static bool IsAggregatorLinkSet(List<LinkInfo> contentLinks) =>
        contentLinks.Count >= LinkExtractor.AggregatorMinStoryLinks &&
        (double)contentLinks.Count(l => l.IsExternal) / contentLinks.Count >= LinkExtractor.AggregatorExternalShare;

    /// <summary>
    /// workspace-romy.4: the shared visual-hierarchy instruction block. The
    /// prompts previously never mentioned the attached image, so even when a
    /// screenshot made it into the message the model had no reason to ground
    /// its ranking in what the page LOOKS like — the techmeme failure mode
    /// (promo rail above the river) in one sentence.
    /// </summary>
    internal static string VisionGuidance(bool hasScreenshot)
    {
        var sb = new StringBuilder();
        if (hasScreenshot)
        {
            sb.AppendLine("A SCREENSHOT of the page is attached — treat it as ground truth for");
            sb.AppendLine("visual hierarchy. The lead story is the large-type item in the main");
            sb.AppendLine("column near the top. Sponsor posts, event calendars, job boards, and");
            sb.AppendLine("other promo slots sit in narrow side rails or visually separate boxes");
            sb.AppendLine("even when their markup mimics stories — they must NEVER lead the");
            sb.AppendLine("layout or sit above real stories. If the screenshot carries small");
            sb.AppendLine("numbered badges, each badge is the index of that link in the list.");
        }

        sb.AppendLine("Links may carry a `vis:` line — measured page geometry (x/y document");
        sb.AppendLine("position, width x height, font size with 'b' for bold, above-fold).");
        sb.AppendLine("Use it to separate the main column (wide, large type, small x) from");
        sb.AppendLine("rails (narrow, small type, large x) and to find the lead. Links with");
        sb.AppendLine("`flag=sponsor` matched ad/sponsor heuristics: exclude them or rank them");
        sb.Append("last, and prefer excluding their whole container by parent selector.");
        return sb.ToString();
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

    private static string BuildCuratedSystemPrompt(bool hasScreenshot)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are curating a news homepage's links for a terminal-based reader.");
        sb.AppendLine(VisionGuidance(hasScreenshot));
        sb.AppendLine();
        sb.AppendLine("Each link is given with a `score` (0-100) — an upstream estimate of");
        sb.AppendLine("visual/editorial prominence (higher = larger/more featured on the page)");
        sb.AppendLine("— and a `sect` (the nearest section heading above the link, or '-' when");
        sb.AppendLine("none). Use `score`, `sect`, the link text, the URL path, the parent CSS");
        sb.AppendLine("selector, and the screenshot together to judge prominence.");
        sb.AppendLine();
        sb.AppendLine("Do TWO things:");
        sb.AppendLine();
        sb.AppendLine("1. EXCLUDE: identify links that are NOT individual news stories. Return");
        sb.AppendLine("   their numeric indices in `excluded`. Typical non-editorial links on a");
        sb.AppendLine("   homepage include:");
        sb.AppendLine("     - \"Subscribe\" / newsletter / account / sign-in / app-download CTAs");
        sb.AppendLine("     - section index/hub pages (e.g. a bare \"/opinion\" or \"/sports\" link)");
        sb.AppendLine("     - author/byline pages and tag/topic landing pages");
        sb.AppendLine("     - /video/ or podcast hub links, live-blog index shells");
        sb.AppendLine("     - podcast EPISODES and 'featured podcasts' / audio-player rails (these are");
        sb.AppendLine("       not news stories even when each row links somewhere)");
        sb.AppendLine("     - social, share, login, and other navigation/utility links");
        sb.AppendLine("     - sponsor posts / advertiser slots, and tertiary site chrome such as");
        sb.AppendLine("       a parent-company link, 'about', leaderboards, or event calendars");
        sb.AppendLine("   A real homepage usually contains a NON-TRIVIAL share of such links —");
        sb.AppendLine("   expect to exclude several. Be aggressive: if it is not a specific");
        sb.AppendLine("   story, it goes in `excluded`. But do NOT invent exclusions for links");
        sb.AppendLine("   that ARE stories just to pad the list.");
        sb.AppendLine();
        sb.AppendLine("2. RANK: of the REMAINING links (the actual stories), order them by");
        sb.AppendLine("   editorial prominence. The single most prominent lead/hero story first,");
        sb.AppendLine("   then the main feed, then secondary, then sidebar. Prefer higher-`score`");
        sb.AppendLine("   items near the top. Return indices in `stories`. This ordering should");
        sb.AppendLine("   reflect a human editor's front page — it will usually DIFFER from the");
        sb.AppendLine("   raw input order.");
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
        sb.AppendLine("Content links extracted from the page (numbered for reference;");
        sb.AppendLine("same-site URLs shown as path only; off-site links keep their host —");
        sb.AppendLine("on aggregator pages the host itself is signal):");
        sb.AppendLine();
        for (int i = 0; i < contentLinks.Count; i++)
        {
            var link = contentLinks[i];
            var sect = string.IsNullOrWhiteSpace(link.SectionTitle) ? "-" : link.SectionTitle;
            sb.AppendLine(
                $"[{i}] score={link.ImportanceScore}{FormatLinkFlags(link)} sect=\"{sect}\" \"{link.DisplayText}\" -> {FormatLinkUrl(link)}");
            if (!string.IsNullOrEmpty(link.ParentSelector))
            {
                sb.AppendLine($"    parent: {link.ParentSelector}");
            }

            // workspace-romy.2: measured geometry — the durable prominence
            // signal (a wide 18px main-column headline vs a narrow 12px rail
            // item). Omitted when the page was fetched without a browser.
            if (link.Geometry is { } g)
            {
                var bold = g.IsBold ? "b" : string.Empty;
                var fold = g.AboveFold ? " above-fold" : string.Empty;
                sb.AppendLine($"    vis: x={g.X} y={g.Y} {g.Width}x{g.Height} font={g.FontSize}{bold}{fold}");
            }
        }

        if (IsAggregatorLinkSet(contentLinks))
        {
            sb.AppendLine();
            sb.AppendLine("NOTE: most stories on this page link to OTHER sites — this is an");
            sb.AppendLine("AGGREGATOR (like Techmeme or Hacker News). The external story links ARE");
            sb.AppendLine("the content. Because story URLs span many domains, shared URL path");
            sb.AppendLine("patterns will NOT generalize here — identify sections by parent CSS");
            sb.AppendLine("selector instead, and leave url_pattern/url_patterns empty.");
        }

        return sb.ToString();
    }

    /// <summary>
    /// workspace-romy.4: inline flags after the score. flag=sponsor marks links
    /// whose text/container matched the ad/sponsor heuristics — kept in the
    /// list (instead of silently dropped) precisely so the model can demote or
    /// exclude the whole slot by its durable selector.
    /// </summary>
    private static string FormatLinkFlags(LinkInfo link) =>
        link.IsSponsored ? " flag=sponsor" : string.Empty;

    /// <summary>
    /// Same-site links render as path-only (saves tokens, surfaces path patterns);
    /// off-site links keep their host — on an aggregator the host IS the story's
    /// identity and a bare path like '/2024/06/post' would be meaningless.
    /// </summary>
    private static string FormatLinkUrl(LinkInfo link)
    {
        if (!link.IsExternal)
        {
            return ToPathOnly(link.Url);
        }

        return Uri.TryCreate(link.Url, UriKind.Absolute, out var uri)
            ? uri.Host + uri.PathAndQuery
            : link.Url;
    }

    /// <summary>
    /// workspace-5oe9.4: strips scheme + host from an absolute URL, leaving the
    /// path (and query). Saves prompt tokens and surfaces the path patterns the
    /// model uses to spot section hubs. Returns the input unchanged when it is
    /// not an absolute URL.
    /// </summary>
    private static string ToPathOnly(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri.PathAndQuery
            : url;
    }

    private static string BuildSetupQuestionsSystemPrompt(int maxQuestions, bool hasScreenshot)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are setting up a news homepage for a terminal reader by inferring a REUSABLE pattern.");
        sb.AppendLine("Each link has a score (0-100 prominence), a sect (nearest heading or '-'), text, and a path-only URL.");
        sb.AppendLine(VisionGuidance(hasScreenshot));
        sb.AppendLine();
        sb.AppendLine("Return TWO things:");
        sb.AppendLine();
        sb.AppendLine("1. proposed_pattern: your best initial reading —");
        sb.AppendLine("   - top_story: the single most prominent lead story;");
        sb.AppendLine("   - tiers: ordered groups of remaining stories (most prominent first);");
        sb.AppendLine("   - exclude: links that are NOT stories (subscribe/newsletter CTAs, section");
        sb.AppendLine("     hub/index pages, author/tag pages, video hubs, sign-in, social/nav,");
        sb.AppendLine("     sponsor/advertiser slots, podcast episodes and 'featured podcasts' /");
        sb.AppendLine("     audio-player rails, and event-calendar / job-board boxes).");
        sb.AppendLine("   For EVERY option set a DURABLE identifier: parent_selector = the shortest");
        sb.AppendLine("   class/id-bearing CSS fragment shared by those links (e.g. 'section.lead',");
        sb.AppendLine("   'section.feed'); url_pattern = a SHORT shared HUB segment like '/opinion/'");
        sb.AppendLine("   (NEVER a single story's full path or headline slug) when one exists, else");
        sb.AppendLine("   empty string. Always fill example_link_indices with the link");
        sb.AppendLine("   numbers the option refers to. These identifiers must generalize to a LATER");
        sb.AppendLine("   visit when the article URLs have changed — never identify a story by its");
        sb.AppendLine("   exact URL. On AGGREGATOR pages (stories link to many different hosts),");
        sb.AppendLine("   url_pattern cannot generalize — leave it empty and identify sections by");
        sb.AppendLine("   parent_selector alone. Aggregator clusters (a lead headline plus related");
        sb.AppendLine("   coverage/discussion sublinks) map naturally to tiers: leads in a top tier,");
        sb.AppendLine("   secondary coverage in a lower (or excluded) tier.");
        sb.AppendLine();
        sb.AppendLine($"2. questions: AT MOST {maxQuestions} questions, and ONLY where you are genuinely");
        sb.AppendLine("   torn between concrete alternatives. A question must DISCRIMINATE: each");
        sb.AppendLine("   answer must produce a materially different layout (different lead, different");
        sb.AppendLine("   grouping, something hidden or kept). The user answers by LOOKING at the");
        sb.AppendLine("   page — every option is highlighted live — so options MUST carry durable");
        sb.AppendLine("   identifiers (parent_selector and/or url_pattern) pointing at real elements");
        sb.AppendLine("   (a hide-or-keep pair may reference the same element). Never ask 'does this");
        sb.AppendLine("   look right?', never offer a lone yes/no, never ask anything whose answer");
        sb.AppendLine("   would not change the sections you output. Set default_answer to your best");
        sb.AppendLine("   guess. Use kind: pick_main (which of these is the lead), confirm_exclude");
        sb.AppendLine("   (hide this group or keep it), confirm_order (which section comes first),");
        sb.AppendLine("   group_by (group by these containers or those).");
        sb.AppendLine();
        sb.Append("If the pattern is obvious, return an EMPTY questions array — that is the best outcome.");
        return sb.ToString();
    }

    private static string BuildInferPatternSystemPrompt(bool hasScreenshot)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are finalizing a REUSABLE homepage layout pattern from the user's answers.");
        sb.AppendLine(VisionGuidance(hasScreenshot));
        sb.AppendLine();
        sb.AppendLine("Return durable sections (most prominent first):");
        sb.AppendLine("- name: short label (the first/lead section should be the single top story);");
        sb.AppendLine("- parent_selectors: shortest class/id-bearing CSS fragments identifying the");
        sb.AppendLine("  section's links (e.g. 'section.lead'); url_patterns: a SHORT path SEGMENT");
        sb.AppendLine("  every story in the section shares (e.g. '/opinion/', '/tech/'). A url_pattern");
        sb.AppendLine("  is a reusable hub segment, NEVER a single story's full path or headline slug");
        sb.AppendLine("  (e.g. '/us-lifts-block-on-mythos-5/' identifies ONE article and is dead on the");
        sb.AppendLine("  next visit) — when stories share no short segment, leave url_patterns EMPTY and");
        sb.AppendLine("  rely on parent_selectors. Set at least one durable identifier per section;");
        sb.AppendLine("- story_indices: the link numbers currently in this section (so the identifier");
        sb.AppendLine("  can be re-derived if needed);");
        sb.AppendLine("- start_collapsed: true for lower-priority STORY sections (real stories that");
        sb.AppendLine("  belong below the fold) — NOT a way to bury non-stories.");
        sb.AppendLine();
        sb.AppendLine("CRITICAL: sponsor/advertiser posts, podcast or audio rails, and");
        sb.AppendLine("event-calendar / job-board boxes are NOT sections — not even collapsed ones.");
        sb.AppendLine("Put their container selector in exclude_selectors so they vanish; never emit a");
        sb.AppendLine("section named 'Sponsors', 'Podcasts', 'Promos', 'Events', etc. A reader wants");
        sb.AppendLine("stories only — a collapsed 'Podcasts' section is still clutter, not curation.");
        sb.AppendLine();
        sb.AppendLine("Also return exclude_selectors / exclude_url_patterns (durable identifiers for the");
        sb.AppendLine("non-stories to hide: utility links, sponsor/podcast/event rails, tertiary chrome");
        sb.AppendLine("like a parent-company link) and exclude_indices (their link numbers). NEVER identify a");
        sb.AppendLine("link by its exact URL — only by selector or path pattern. On AGGREGATOR pages");
        sb.AppendLine("(stories span many hosts) url_patterns cannot generalize — use parent_selectors");
        sb.AppendLine("only. Honour the user's answers.");
        sb.AppendLine();
        sb.AppendLine("Also return `confidence` (0..1, how sure you are this layout matches the");
        sb.AppendLine("page's editorial hierarchy) and `confirm_question`: null when confident,");
        sb.AppendLine("or ONE targeted question (e.g. 'Which of these is the main story?') with");
        sb.AppendLine("2-4 options when genuinely torn. Each option needs a label plus a durable");
        sb.AppendLine("parent_selector and/or url_pattern pointing at real elements. The user can");
        sb.Append("ignore the question and save as-is, so never gate the layout on it.");
        return sb.ToString();
    }

    private static string BuildInferPatternUserPrompt(
        List<LinkInfo> contentLinks,
        string pageUrl,
        SiteSetupProposal proposal,
        IReadOnlyList<SetupAnswer> answers)
    {
        var sb = new StringBuilder();
        sb.Append(BuildCuratedUserPrompt(contentLinks, pageUrl));
        sb.AppendLine();
        sb.AppendLine("Your proposed pattern was:");
        if (proposal.ProposedPattern.TopStory is { } top)
        {
            sb.AppendLine($"  top_story: {DescribeOption(top)}");
        }

        foreach (var tier in proposal.ProposedPattern.Tiers)
        {
            sb.AppendLine($"  tier: {DescribeOption(tier)}");
        }

        foreach (var ex in proposal.ProposedPattern.Exclude)
        {
            sb.AppendLine($"  exclude: {DescribeOption(ex)}");
        }

        sb.AppendLine();
        var byId = proposal.Questions.ToDictionary(q => q.Id, q => q, StringComparer.Ordinal);
        var qaAnswers = answers.Where(a => !string.Equals(a.QuestionId, "adjustment", StringComparison.Ordinal)).ToList();
        var userInstructions = answers
            .Where(a => string.Equals(a.QuestionId, "adjustment", StringComparison.Ordinal))
            .ToList();

        if (qaAnswers.Count > 0)
        {
            sb.AppendLine("The user answered your questions as follows:");
            foreach (var answer in qaAnswers)
            {
                var promptText = byId.TryGetValue(answer.QuestionId, out var q) ? q.Prompt : answer.QuestionId;
                sb.AppendLine($"  Q: {promptText}");
                sb.AppendLine($"  A: {answer.Answer}");
            }

            sb.AppendLine();
        }

        if (userInstructions.Count > 0)
        {
            // workspace-q77e: the user's free-text steering is a DIRECT command,
            // not just another answer. Surfacing it as a buried 'Q: adjustment'
            // line let a weak model under-weight it; promote it to the
            // highest-priority directive so the revised pattern actually obeys
            // (drop/keep the named things, re-order, change the lead).
            sb.AppendLine("DIRECT USER INSTRUCTION(S) — HIGHEST PRIORITY. The user typed these to change");
            sb.AppendLine("the layout. Revise the pattern to satisfy them even when that overrides your");
            sb.AppendLine("earlier proposal or the answers above — e.g. add the named things to");
            sb.AppendLine("exclude_selectors/exclude_indices, drop a section, or re-order/re-pick the lead:");
            foreach (var instruction in userInstructions)
            {
                sb.AppendLine($"  • {instruction.Answer}");
            }

            sb.AppendLine();
        }

        sb.Append("Produce the final durable pattern, honouring the user's input above.");
        return sb.ToString();
    }

    /// <summary>
    /// workspace-q77e: the refine prompt EDITS an already-approved layout. Its
    /// whole job is to change exactly what the user asked and leave the rest
    /// byte-for-byte the same, so refinement is incremental, not a fresh guess.
    /// </summary>
    private static string BuildRefineSystemPrompt(bool hasScreenshot)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are EDITING a homepage layout the user already approved — not building one from scratch.");
        sb.AppendLine(VisionGuidance(hasScreenshot));
        sb.AppendLine();
        sb.AppendLine("You are given the CURRENT layout (sections in order, each with durable selectors /");
        sb.AppendLine("url_patterns + start_collapsed, plus the exclude lists) and ONE change the user typed.");
        sb.AppendLine("Apply the SMALLEST edit that satisfies that change and KEEP EVERYTHING ELSE IDENTICAL:");
        sb.AppendLine("- echo every untouched section's `name` field BYTE-FOR-BYTE from the current layout");
        sb.AppendLine("  (do not reword, shorten, or 'improve' a name you weren't asked to change), and copy");
        sb.AppendLine("  its order, parent_selectors, url_patterns and start_collapsed EXACTLY — never");
        sb.AppendLine("  reorder, re-derive or drop a section the change didn't mention;");
        sb.AppendLine("- keep the existing exclude_selectors / exclude_url_patterns, only adding/removing the");
        sb.AppendLine("  ones the change requires;");
        sb.AppendLine("- never throw away stories that are currently shown unless the user asked to hide them.");
        sb.AppendLine();
        sb.AppendLine("Return the FULL layout in the schema (all sections + exclude lists), i.e. the current");
        sb.AppendLine("one with the user's change applied. Keep identifiers durable: parent_selectors are the");
        sb.AppendLine("shortest class/id CSS fragments; a url_pattern is a SHORT shared hub segment (e.g.");
        sb.AppendLine("'/opinion/'), NEVER a single story's slug; on aggregators use parent_selectors only.");
        sb.AppendLine("Sponsor/advert/promo, podcast/audio, and event-calendar rails go in exclude_selectors,");
        sb.AppendLine("never a section. Set start_collapsed only for lower-priority STORY sections.");
        sb.AppendLine();
        sb.AppendLine("Set `confidence` and leave `confirm_question` null unless the change is genuinely");
        sb.Append("ambiguous (then ONE short question with durable options).");
        return sb.ToString();
    }

    private static string BuildRefineUserPrompt(
        List<LinkInfo> contentLinks, string pageUrl, SiteHierarchyConfig currentConfig, string instruction)
    {
        var sb = new StringBuilder();
        sb.Append(BuildCuratedUserPrompt(contentLinks, pageUrl));
        sb.AppendLine();
        sb.AppendLine("CURRENT LAYOUT (the user approved this — preserve it except for the change below):");
        if (currentConfig.Sections.Count == 0)
        {
            sb.AppendLine("  (no sections yet)");
        }

        for (int i = 0; i < currentConfig.Sections.Count; i++)
        {
            var s = currentConfig.Sections[i];
            var ids = new List<string>();
            if (s.ParentSelectors.Count > 0)
            {
                ids.Add($"selectors=[{string.Join(" , ", s.ParentSelectors)}]");
            }

            if (s.UrlPatterns.Count > 0)
            {
                ids.Add($"url_patterns=[{string.Join(" , ", s.UrlPatterns)}]");
            }

            var collapsed = s.StartCollapsed ? " (collapsed)" : string.Empty;
            sb.AppendLine($"  section {i + 1}: \"{s.Name}\"{collapsed} {string.Join(" ", ids)}");
        }

        if (currentConfig.ExcludeSelectors.Count > 0)
        {
            sb.AppendLine($"  exclude_selectors=[{string.Join(" , ", currentConfig.ExcludeSelectors)}]");
        }

        if (currentConfig.ExcludeUrlPatterns.Count > 0)
        {
            sb.AppendLine($"  exclude_url_patterns=[{string.Join(" , ", currentConfig.ExcludeUrlPatterns)}]");
        }

        sb.AppendLine();
        sb.AppendLine("THE ONE CHANGE THE USER TYPED (apply only this, keep the rest identical):");
        sb.AppendLine($"  • {instruction.Trim()}");
        sb.AppendLine();
        sb.Append("Return the full edited layout.");
        return sb.ToString();
    }

    private static string DescribeOption(SetupOption option)
    {
        var idParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(option.ParentSelector))
        {
            idParts.Add($"selector={option.ParentSelector}");
        }

        if (!string.IsNullOrWhiteSpace(option.UrlPattern))
        {
            idParts.Add($"url={option.UrlPattern}");
        }

        var idText = idParts.Count > 0 ? $" [{string.Join(", ", idParts)}]" : string.Empty;
        var indices = option.ExampleLinkIndices.Count > 0
            ? $" (links {string.Join(",", option.ExampleLinkIndices)})"
            : string.Empty;
        return $"{option.Label}{idText}{indices}";
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

    private ChatCompletionOptions BuildOptions(
        string schemaName,
        BinaryData schema,
        string schemaDescription,
        string? reasoningEffortOverride = null,
        int? maxOutputTokensOverride = null)
    {
        var options = new ChatCompletionOptions
        {
            ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                jsonSchemaFormatName: schemaName,
                jsonSchema: schema,
                jsonSchemaFormatDescription: schemaDescription,
                jsonSchemaIsStrict: true),
            MaxOutputTokenCount = maxOutputTokensOverride is > 0
                ? Math.Max(_config.MaxTokens, maxOutputTokensOverride.Value)
                : _config.MaxTokens,
        };

        // ReasoningEffortLevel is gpt-5 / o-series only; the SDK's "Minimal"
        // member is marked experimental but is exactly the right knob for
        // classification work. Anything else falls back to no override.
        // workspace-5oe9.4: a per-call override lets the one-time AI setup
        // request a higher effort (e.g. "low") for genuine reordering while the
        // config default stays "minimal" for cheap revisits.
        var effort = string.IsNullOrWhiteSpace(reasoningEffortOverride)
            ? _config.ReasoningEffort
            : reasoningEffortOverride;
#pragma warning disable OPENAI001 // Experimental API
        if (TryMapReasoningEffort(effort, out var level))
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

        var urlPattern = ScrapingStrategies.DocumentOrderStrategy.BuildUrlPattern(pageUrl);

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

    /// <summary>
    /// workspace-5oe9.7: parses the round-1 setup response and CLAMPS it to the
    /// configured limits — at most <paramref name="maxQuestions"/> questions,
    /// at most <see cref="MaxOptionsPerQuestion"/> options each, and at most
    /// <see cref="MaxExampleIndicesPerOption"/> indices per option — so a
    /// misbehaving model can never blow past the wizard's bounds.
    /// </summary>
    internal static SiteSetupProposal ParseSetupQuestions(string responseText, int maxQuestions)
    {
        var jsonText = StripOptionalFences(responseText);
        var parsed = JsonSerializer.Deserialize<SetupQuestionsResponse>(jsonText, ParseOptions)
            ?? throw new JsonException("Failed to deserialize setup-questions response");

        var pattern = parsed.ProposedPattern ?? new ProposedPatternResponse();
        var proposed = new ProposedPattern
        {
            TopStory = pattern.TopStory != null ? MapOption(pattern.TopStory) : null,
            Tiers = (pattern.Tiers ?? new()).Select(MapOption).ToList(),
            Exclude = (pattern.Exclude ?? new()).Select(MapOption).ToList(),
        };

        var questions = (parsed.Questions ?? new())
            .Take(Math.Max(0, maxQuestions))
            .Select(q => new SetupQuestion
            {
                Id = string.IsNullOrWhiteSpace(q.Id) ? Guid.NewGuid().ToString("N")[..8] : q.Id!,
                Prompt = q.Prompt ?? string.Empty,
                Kind = MapQuestionKind(q.Kind),
                DefaultAnswer = q.DefaultAnswer ?? string.Empty,
                Options = (q.Options ?? new())
                    .Take(MaxOptionsPerQuestion)
                    .Select(MapOption)
                    .ToList(),
            })
            .ToList();

        return new SiteSetupProposal { ProposedPattern = proposed, Questions = questions };
    }

    /// <summary>
    /// workspace-5oe9.7: parses the round-2 response into a durable
    /// <see cref="SiteHierarchyConfig"/>. Prefers model-returned selectors;
    /// falls back to <see cref="SelectorDerivation"/> over a section's
    /// story_indices when the model omitted them. workspace-romy.8: wrapped in
    /// <see cref="InferredPattern"/> with confidence + optional confirm question.
    /// </summary>
    internal static InferredPattern ParsePatternFromAnswers(
        string responseText, List<LinkInfo> contentLinks, string pageUrl, string modelVersion)
    {
        var jsonText = StripOptionalFences(responseText);
        var parsed = JsonSerializer.Deserialize<SetupPatternResponse>(jsonText, ParseOptions)
            ?? throw new JsonException("Failed to deserialize infer-pattern response");

        List<LinkInfo> LinksAt(IEnumerable<int>? indices) =>
            (indices ?? Enumerable.Empty<int>())
                .Where(i => i >= 0 && i < contentLinks.Count)
                .Select(i => contentLinks[i])
                .ToList();

        var sections = new List<HierarchySection>();
        var railExcludeSelectors = new List<string>();
        var railExcludeUrlPatterns = new List<string>();
        foreach (var s in parsed.Sections ?? new())
        {
            var members = LinksAt(s.StoryIndices);

            // workspace-romy.10: sanitize model selectors — date/item-stamped
            // id fragments (div.item#260611p108) match one link today and
            // nothing tomorrow; stripping them only broadens the match.
            var selectors = (s.ParentSelectors ?? new())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(SelectorDerivation.StripVolatileIds)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            // workspace-q77e: a model that returns a section's lead-article slug
            // as a url_pattern pins the section to ONE story — durable today,
            // dead tomorrow. Strip those the same way StripVolatileIds strips
            // date-stamped ids; an emptied list then re-derives a real shared
            // segment below.
            var urlPatterns = SelectorDerivation.StripVolatileUrlPatterns(s.UrlPatterns ?? new());

            // B3 fallback only when the model omitted durable identifiers.
            if (selectors.Count == 0 && members.Count > 0)
            {
                selectors = SelectorDerivation.DeriveParentSelectors(members);
            }

            // workspace-q77e: only derive a url_pattern when the section has NO
            // durable selector to stand on. Bolting a derived path onto a
            // selector-identified section adds nothing but risk — for a
            // one-story section (e.g. the lead) DeriveUrlPatterns re-emits that
            // story's own slug, undoing the volatile-pattern strip above.
            if (urlPatterns.Count == 0 && selectors.Count == 0 && members.Count > 0)
            {
                urlPatterns = SelectorDerivation.DeriveUrlPatterns(members);
            }

            if (selectors.Count == 0 && urlPatterns.Count == 0)
            {
                continue; // no durable identifier — skip this section
            }

            // workspace-q77e: a sponsor / podcast / promo / calendar rail is NOT
            // a section — the model keeps trying to "hide" it as a collapsed
            // low-priority section (it even names them "promos", "Featured
            // podcasts", "(keep out)"), which still renders the junk. When the
            // model's own name marks a section as such a rail, route its durable
            // identifiers to the exclude lists instead. Never demote the lead
            // (the first kept section), so a real layout always survives.
            if (sections.Count > 0 && IsNonStoryRailSectionName(s.Name))
            {
                railExcludeSelectors.AddRange(selectors);
                railExcludeUrlPatterns.AddRange(urlPatterns);
                continue;
            }

            sections.Add(new HierarchySection
            {
                Name = string.IsNullOrWhiteSpace(s.Name) ? $"Section {sections.Count + 1}" : s.Name!,
                SortOrder = sections.Count,
                ParentSelectors = selectors,
                UrlPatterns = urlPatterns,
                StartCollapsed = s.StartCollapsed,
            });
        }

        var excludeSelectors = (parsed.ExcludeSelectors ?? new())
            .Concat(railExcludeSelectors)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(SelectorDerivation.StripVolatileIds)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var excludeUrlPatterns = (parsed.ExcludeUrlPatterns ?? new())
            .Concat(railExcludeUrlPatterns)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // Fall back to deriving exclude identifiers from indices when the model
        // gave none.
        if (excludeSelectors.Count == 0 && excludeUrlPatterns.Count == 0)
        {
            var excludedLinks = LinksAt(parsed.ExcludeIndices);
            excludeSelectors = excludedLinks
                .SelectMany(l => SelectorDerivation.DiscriminatingTokens(l.ParentSelector))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            excludeUrlPatterns = excludedLinks
                .SelectMany(l => SelectorDerivation.MeaningfulPathSegments(l.Url))
                .Distinct(StringComparer.Ordinal)
                .Select(s => $"/{s}/")
                .ToList();
        }

        // workspace-9wm6: drop an over-broad exclude rule before it nukes real
        // stories. The model occasionally excludes a whole story CONTAINER (e.g.
        // 'div.itc1 > div.ifsp' on techmeme), collapsing coverage to a fraction
        // of the page. A rule that would hide a large share of the page's
        // HIGH-importance links (score >= 80 — genuine stories) is wrong by
        // construction; keep only the surgical ones that target chrome/sponsors.
        var highScore = contentLinks.Where(l => l.ImportanceScore >= 80 && !l.IsGroupHeader).ToList();
        if (highScore.Count > 0)
        {
            var maxHidden = highScore.Count * 0.25;
            excludeSelectors = excludeSelectors
                .Where(sel => highScore.Count(l =>
                    !string.IsNullOrEmpty(l.ParentSelector)
                    && l.ParentSelector.Contains(sel, StringComparison.OrdinalIgnoreCase)) <= maxHidden)
                .ToList();
            excludeUrlPatterns = excludeUrlPatterns
                .Where(pat => highScore.Count(l =>
                    l.Url.Contains(pat, StringComparison.OrdinalIgnoreCase)) <= maxHidden)
                .ToList();
        }

        // workspace-9wm6: a "lead" section is the SINGLE top story. When the
        // model's first section has a too-broad selector that matches several
        // headlines, pin it to one — but ONLY when every overflow link is also
        // claimed by a later section, so tightening the lead never hides a story
        // (the capped section re-offers its overflow downstream in the builder).
        if (sections.Count >= 2)
        {
            bool Excluded(LinkInfo l) =>
                excludeSelectors.Any(sel => !string.IsNullOrEmpty(l.ParentSelector)
                    && l.ParentSelector.Contains(sel, StringComparison.OrdinalIgnoreCase))
                || excludeUrlPatterns.Any(pat => l.Url.Contains(pat, StringComparison.OrdinalIgnoreCase));

            var visible = contentLinks.Where(l => !l.IsGroupHeader && !Excluded(l)).ToList();
            var leadMatches = visible
                .Where(l => NavigationTreeBuilder.MatchesSection(l, sections[0]))
                .ToList();
            if (leadMatches.Count > 1
                && leadMatches.Skip(1).All(l =>
                    sections.Skip(1).Any(sec => NavigationTreeBuilder.MatchesSection(l, sec))))
            {
                sections[0] = sections[0] with { MaxLinks = 1 };
            }
        }

        var domain = SafeHost(pageUrl);
        var config = new SiteHierarchyConfig
        {
            Domain = domain,
            UrlPattern = ScrapingStrategies.DocumentOrderStrategy.BuildUrlPattern(pageUrl),
            Sections = sections,
            ExcludeSelectors = excludeSelectors,
            ExcludeUrlPatterns = excludeUrlPatterns,
            CreatedAt = DateTime.UtcNow,
            ModelVersion = modelVersion,
            Kind = LayoutKind.AiCurated,
            Version = 3,
            Strategy = ScrapingStrategies.AiCuratedStrategy.StrategyId,
        };

        // workspace-romy.8: an optional targeted confirm question, shown as
        // ignorable rows on the preview card. Clamped like round-1 questions.
        SetupQuestion? confirmQuestion = null;
        if (parsed.ConfirmQuestion is { } cq && !string.IsNullOrWhiteSpace(cq.Prompt))
        {
            confirmQuestion = new SetupQuestion
            {
                Id = "confirm",
                Prompt = cq.Prompt!,
                Kind = SetupQuestionKind.PickMain,
                Options = (cq.Options ?? new())
                    .Take(MaxOptionsPerQuestion)
                    .Select(MapOption)
                    .ToList(),
            };
        }

        return new InferredPattern
        {
            Config = config,
            Confidence = Math.Clamp(parsed.Confidence ?? 1.0, 0.0, 1.0),
            ConfirmQuestion = confirmQuestion,
        };
    }

    private static SetupOption MapOption(OptionResponse o) => new()
    {
        Label = o.Label ?? string.Empty,
        ParentSelector = SelectorDerivation.StripVolatileIds(o.ParentSelector ?? string.Empty),
        UrlPattern = o.UrlPattern ?? string.Empty,
        ExampleLinkIndices = (o.ExampleLinkIndices ?? new()).Take(MaxExampleIndicesPerOption).ToList(),
    };

    private static SetupQuestionKind MapQuestionKind(string? kind) => kind switch
    {
        "pick_main" => SetupQuestionKind.PickMain,
        "confirm_exclude" => SetupQuestionKind.ConfirmExclude,
        "confirm_order" => SetupQuestionKind.ConfirmOrder,
        "group_by" => SetupQuestionKind.GroupBy,
        _ => SetupQuestionKind.PickMain,
    };

    /// <summary>
    /// workspace-q77e: true when the model's own section NAME marks it as a
    /// non-story rail (sponsor/advert/promo/podcast/audio/calendar). The model
    /// reliably labels these rails descriptively but then keeps them as a
    /// collapsed section; this lets the parser route them to the exclude lists.
    /// </summary>
    internal static bool IsNonStoryRailSectionName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && NonStoryRailName.IsMatch(name);

    private static string SafeHost(string url) => HierarchyDomainKey.FromUrl(url);
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

    // workspace-frpl.10 (B9b): single-label classify response.
    private sealed class SectionClassificationResponse
    {
        [JsonPropertyName("label")]
        public string? Label { get; set; }

        [JsonPropertyName("confidence")]
        public double Confidence { get; set; }
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

    [System.Diagnostics.CodeAnalysis.SuppressMessage("SonarAnalyzer.CSharp", "S3459:Unassigned members should be removed", Justification = "Assigned by JSON deserialization")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SonarAnalyzer.CSharp", "S1144:Unused private types or members should be removed", Justification = "Read by deserialization")]
    private sealed class OptionResponse
    {
        [JsonPropertyName("label")]
        public string? Label { get; set; }

        [JsonPropertyName("parent_selector")]
        public string? ParentSelector { get; set; }

        [JsonPropertyName("url_pattern")]
        public string? UrlPattern { get; set; }

        [JsonPropertyName("example_link_indices")]
        public List<int>? ExampleLinkIndices { get; set; }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("SonarAnalyzer.CSharp", "S3459:Unassigned members should be removed", Justification = "Assigned by JSON deserialization")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SonarAnalyzer.CSharp", "S1144:Unused private types or members should be removed", Justification = "Read by deserialization")]
    private sealed class ProposedPatternResponse
    {
        [JsonPropertyName("top_story")]
        public OptionResponse? TopStory { get; set; }

        [JsonPropertyName("tiers")]
        public List<OptionResponse>? Tiers { get; set; }

        [JsonPropertyName("exclude")]
        public List<OptionResponse>? Exclude { get; set; }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("SonarAnalyzer.CSharp", "S3459:Unassigned members should be removed", Justification = "Assigned by JSON deserialization")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SonarAnalyzer.CSharp", "S1144:Unused private types or members should be removed", Justification = "Read by deserialization")]
    private sealed class SetupQuestionResponse
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("prompt")]
        public string? Prompt { get; set; }

        [JsonPropertyName("kind")]
        public string? Kind { get; set; }

        [JsonPropertyName("default_answer")]
        public string? DefaultAnswer { get; set; }

        [JsonPropertyName("options")]
        public List<OptionResponse>? Options { get; set; }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("SonarAnalyzer.CSharp", "S3459:Unassigned members should be removed", Justification = "Assigned by JSON deserialization")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SonarAnalyzer.CSharp", "S1144:Unused private types or members should be removed", Justification = "Read by deserialization")]
    private sealed class SetupQuestionsResponse
    {
        [JsonPropertyName("proposed_pattern")]
        public ProposedPatternResponse? ProposedPattern { get; set; }

        [JsonPropertyName("questions")]
        public List<SetupQuestionResponse>? Questions { get; set; }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("SonarAnalyzer.CSharp", "S3459:Unassigned members should be removed", Justification = "Assigned by JSON deserialization")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SonarAnalyzer.CSharp", "S1144:Unused private types or members should be removed", Justification = "Read by deserialization")]
    private sealed class SetupSectionResponse
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("parent_selectors")]
        public List<string>? ParentSelectors { get; set; }

        [JsonPropertyName("url_patterns")]
        public List<string>? UrlPatterns { get; set; }

        [JsonPropertyName("story_indices")]
        public List<int>? StoryIndices { get; set; }

        [JsonPropertyName("start_collapsed")]
        public bool StartCollapsed { get; set; }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("SonarAnalyzer.CSharp", "S3459:Unassigned members should be removed", Justification = "Assigned by JSON deserialization")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SonarAnalyzer.CSharp", "S1144:Unused private types or members should be removed", Justification = "Read by deserialization")]
    private sealed class SetupPatternResponse
    {
        [JsonPropertyName("sections")]
        public List<SetupSectionResponse>? Sections { get; set; }

        [JsonPropertyName("exclude_selectors")]
        public List<string>? ExcludeSelectors { get; set; }

        [JsonPropertyName("exclude_url_patterns")]
        public List<string>? ExcludeUrlPatterns { get; set; }

        [JsonPropertyName("exclude_indices")]
        public List<int>? ExcludeIndices { get; set; }

        // workspace-romy.8: self-assessed confidence + at most one targeted
        // confirm question (both optional for older fixtures/responses).
        [JsonPropertyName("confidence")]
        public double? Confidence { get; set; }

        [JsonPropertyName("confirm_question")]
        public ConfirmQuestionResponse? ConfirmQuestion { get; set; }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "SonarAnalyzer.CSharp",
        "S3459:Unassigned members should be removed",
        Justification = "Properties are assigned by JSON deserialization")]
    private sealed class ConfirmQuestionResponse
    {
        [JsonPropertyName("prompt")]
        public string? Prompt { get; set; }

        [JsonPropertyName("options")]
        public List<OptionResponse>? Options { get; set; }
    }
}
