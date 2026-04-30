// Licensed under the MIT License. See LICENSE in the repository root.

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WireCopy.Application.Interfaces;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Configuration;

namespace WireCopy.Infrastructure.Browser;

/// <summary>
/// Analyzes page screenshots using Anthropic's Claude to determine
/// the visual hierarchy of links on a webpage and to curate ad-free
/// story lists for the AI Curated scraping strategy.
/// </summary>
public class AnthropicHierarchyAnalyzer : IHierarchyAnalyzer
{
    private const string AnthropicVersionHeader = "2023-06-01";
    private static readonly Uri ApiEndpoint = new("https://api.anthropic.com/v1/messages");

    private static readonly JsonSerializerOptions ParseOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly AnthropicConfiguration _config;
    private readonly IUserSettingsStore _settingsStore;
    private readonly ILogger<AnthropicHierarchyAnalyzer> _logger;
    private readonly HttpClient _httpClient;

    public AnthropicHierarchyAnalyzer(
        IOptions<AnthropicConfiguration> config,
        IUserSettingsStore settingsStore,
        ILogger<AnthropicHierarchyAnalyzer> logger,
        HttpClient? httpClient = null)
    {
        _config = config.Value;
        _settingsStore = settingsStore;
        _logger = logger;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    }

    /// <inheritdoc />
    public bool IsConfigured
    {
        get
        {
            var key = GetApiKey();
            return !string.IsNullOrWhiteSpace(key);
        }
    }

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
                "Anthropic API key not configured. Use :set anthropic-key to configure.");

        var domain = new Uri(pageUrl).Host.ToLowerInvariant();
        var prompt = BuildPrompt(links, pageUrl);
        if (!string.IsNullOrEmpty(promptSuffix))
        {
            prompt += "\n\n" + promptSuffix;
        }

        var screenshotBase64 = Convert.ToBase64String(screenshot);

        _logger.LogInformation(
            "Analyzing page hierarchy for {Url} ({LinkCount} links, {ScreenshotSize} bytes)",
            pageUrl,
            links.Count,
            screenshot.Length);

        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var responseText = await CallAnthropicApiAsync(
                    apiKey, screenshotBase64, prompt, cancellationToken).ConfigureAwait(false);

                var config = ParseResponse(responseText, domain, pageUrl);

                _logger.LogInformation(
                    "AI hierarchy analysis complete: {SectionCount} sections detected for {Domain}",
                    config.Sections.Count,
                    domain);

                return config;
            }
            catch (JsonException ex) when (attempt == 0)
            {
                _logger.LogWarning(
                    ex, "Malformed JSON in AI response, retrying with stricter prompt");

                var retryNotice = new StringBuilder(prompt);
                retryNotice.AppendLine();
                retryNotice.AppendLine();
                retryNotice.Append("IMPORTANT: Your previous response was not valid JSON. ");
                retryNotice.Append("Please respond with ONLY the JSON array, no markdown fences, no explanation.");
                prompt = retryNotice.ToString();
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
                "Anthropic API key not configured. Use :set anthropic-key to configure.");

        var contentLinks = links
            .Where(l => l.Type == LinkType.Content)
            .ToList();

        var prompt = BuildCuratedPrompt(contentLinks, pageUrl);
        var screenshotBase64 = screenshot is { Length: > 0 }
            ? Convert.ToBase64String(screenshot)
            : null;

        _logger.LogInformation(
            "AI curated analysis for {Url} ({Count} content links, screenshot={HasShot})",
            pageUrl,
            contentLinks.Count,
            screenshotBase64 != null);

        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var responseText = await CallAnthropicApiAsync(
                    apiKey, screenshotBase64, prompt, cancellationToken).ConfigureAwait(false);

                return ParseCuratedResponse(responseText, contentLinks);
            }
            catch (JsonException ex) when (attempt == 0)
            {
                _logger.LogWarning(ex, "Malformed JSON in AI curated response, retrying");
                var sb = new StringBuilder(prompt);
                sb.Append("\n\nIMPORTANT: respond with ONLY the JSON object — no fences, no commentary.");
                prompt = sb.ToString();
            }
        }

        throw new InvalidOperationException("Failed to parse AI curated response after retries");
    }

    private static string BuildPrompt(List<LinkInfo> links, string pageUrl)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are analyzing a webpage screenshot for a terminal-based news reader.");
        sb.AppendLine("Group the content links below by visual section, ordered by editorial prominence.");
        sb.AppendLine();
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

        sb.AppendLine();
        sb.AppendLine("Group these links into sections by editorial prominence:");
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
        sb.AppendLine("- parentSelectors: Common CSS parent selectors from the parent field above");
        sb.AppendLine("- urlPatterns: URL path patterns shared by links (e.g., \"/opinion/\")");
        sb.AppendLine("- startCollapsed: true for less prominent sections (sidebar, below fold)");
        sb.AppendLine();
        sb.AppendLine("Respond with ONLY a JSON array (no markdown fences, no explanation):");
        sb.AppendLine("[");
        sb.AppendLine("  {\"name\": \"Section Name\", \"linkIndices\": [0, 1, 2], \"parentSelectors\": [\"article > h3\"], \"urlPatterns\": [\"/section/\"], \"startCollapsed\": false},");
        sb.AppendLine("  ...");
        sb.AppendLine("]");

        return sb.ToString();
    }

    /// <summary>
    /// AI Curated prompt: ask the model to (1) drop ads/promos and
    /// (2) rank remaining stories by editorial prominence.
    /// </summary>
    private static string BuildCuratedPrompt(List<LinkInfo> contentLinks, string pageUrl)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are curating a webpage's links for a terminal-based news reader.");
        sb.AppendLine();
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
        sb.AppendLine("3. (optional) propose named SECTIONS that group the stories. Skip if not useful.");
        sb.AppendLine();
        sb.AppendLine("Respond with ONLY this JSON object (no fences, no commentary):");
        sb.AppendLine("{");
        sb.AppendLine("  \"excluded\": [3, 7, 12],");
        sb.AppendLine("  \"stories\": [1, 0, 2, 5, 4],");
        sb.AppendLine("  \"sections\": [");
        sb.AppendLine("    {\"name\": \"Lead\", \"story_indices\": [1, 0], \"start_collapsed\": false},");
        sb.AppendLine("    {\"name\": \"Opinion\", \"story_indices\": [9, 10], \"start_collapsed\": true}");
        sb.AppendLine("  ]");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("If you cannot identify sections, return `\"sections\": []`.");
        sb.AppendLine("Every numeric index MUST be a valid index from the list above.");
        sb.AppendLine("`excluded` and `stories` together MUST cover every index exactly once.");

        return sb.ToString();
    }

    private static AiCuratedResult ParseCuratedResponse(string responseText, List<LinkInfo> contentLinks)
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

    private async Task<string> CallAnthropicApiAsync(
        string apiKey,
        string? screenshotBase64,
        string prompt,
        CancellationToken cancellationToken)
    {
        var content = new List<object>();
        if (!string.IsNullOrEmpty(screenshotBase64))
        {
            content.Add(new
            {
                type = "image",
                source = new
                {
                    type = "base64",
                    media_type = "image/png",
                    data = screenshotBase64,
                },
            });
        }

        content.Add(new { type = "text", text = prompt });

        var requestBody = new
        {
            model = _config.Model,
            max_tokens = _config.MaxTokens,
            messages = new[]
            {
                new
                {
                    role = "user",
                    content = content.ToArray(),
                },
            },
        };

        var json = JsonSerializer.Serialize(requestBody);

        using var request = new HttpRequestMessage(HttpMethod.Post, ApiEndpoint);
        request.Headers.Add("x-api-key", apiKey);
        request.Headers.Add("anthropic-version", AnthropicVersionHeader);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError(
                "Anthropic API error {StatusCode}: {Body}", response.StatusCode, responseBody);
            throw new HttpRequestException(
                $"Anthropic API returned {response.StatusCode}: {responseBody}");
        }

        using var doc = JsonDocument.Parse(responseBody);
        var contentArray = doc.RootElement.GetProperty("content");

        var textBlock = contentArray.EnumerateArray()
            .FirstOrDefault(b => b.GetProperty("type").GetString() == "text");

        if (textBlock.ValueKind != JsonValueKind.Undefined)
        {
            return textBlock.GetProperty("text").GetString() ?? string.Empty;
        }

        throw new InvalidOperationException("No text content in Anthropic API response");
    }

    private string? GetApiKey()
    {
        var storedKey = _settingsStore.Get("AnthropicApiKey");
        if (!string.IsNullOrWhiteSpace(storedKey))
        {
            return storedKey;
        }

        return _config.ApiKey;
    }

    private SiteHierarchyConfig ParseResponse(string responseText, string domain, string pageUrl)
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

        var sections = JsonSerializer.Deserialize<List<AiSectionResponse>>(jsonText, ParseOptions)
            ?? throw new JsonException("Failed to deserialize AI response as section array");

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
            ModelVersion = _config.Model,
        };
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
