// <copyright file="CommandOptions.cs" company="TermReader">
// Educational and personal use only.
// </copyright>


using CommandLine;

namespace TermReader.API;

/// <summary>
/// Options for the browse verb - launches terminal browser mode.
/// </summary>
[Verb("browse", HelpText = "Launch terminal browser for interactive web browsing")]
public class BrowseOptions
{
    [Value(0, MetaName = "url", Required = false, HelpText = "Initial URL to load (optional, will prompt if not provided)")]
    public string? Url { get; set; }

    /// <summary>
    /// Validates the browse options and returns validation errors if any.
    /// </summary>
    /// <returns>List of validation error messages, empty if valid.</returns>
    public List<string> Validate()
    {
        var errors = new List<string>();

        // Validate URL format if provided
        if (!string.IsNullOrWhiteSpace(Url))
        {
            if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                errors.Add($"Invalid URL: '{Url}'. Must be a valid HTTP/HTTPS URL.");
            }
        }

        return errors;
    }
}

public class CommandOptions
{
    [Option('u', "url", Required = false, HelpText = "Specific article URL to process")]
    public string? ArticleUrl { get; set; }

    [Option('s', "section", Required = false, HelpText = "Section to scrape (e.g., technology, politics)")]
    public string? Section { get; set; }

    [Option('c', "count", Required = false, Default = 100, HelpText = "Number of articles to process (default: 100, collects all from specified sections)")]
    public int ArticleCount { get; set; }

    [Option('v', "voice", Required = false, HelpText = "ElevenLabs voice ID to use")]
    public string? VoiceId { get; set; }

    [Option('b', "budget", Required = false, HelpText = "Maximum budget in dollars (default: 5.0)")]
    public decimal Budget { get; set; } = 5.0m;

    [Option('o', "output", Required = false, HelpText = "Output directory path")]
    public string? OutputPath { get; set; }

    [Option("skip-login", Required = false, Default = false, HelpText = "Skip login (only scrape public articles)")]
    public bool SkipLogin { get; set; }

    [Option("test", Required = false, Default = false, HelpText = "Run with test/mock data")]
    public bool TestMode { get; set; }

    [Option("cookie-info", Required = false, Default = false, HelpText = "Display information about stored cookies")]
    public bool CookieInfo { get; set; }

    [Option("clear-cookies", Required = false, Default = false, HelpText = "Clear all stored cookies")]
    public bool ClearCookies { get; set; }

    [Option("import-cookies", Required = false, HelpText = "Import cookies from a JSON file (e.g., --import-cookies ~/nyt-cookies.json)")]
    public string? ImportCookiesPath { get; set; }

    [Option("scrape-only", Required = false, Default = false, HelpText = "Only scrape articles without generating audio (for testing)")]
    public bool ScrapeOnly { get; set; }

    [Option("audio-only", Required = false, Default = false, HelpText = "Generate audio from previously scraped articles (skip scraping)")]
    public bool AudioOnly { get; set; }

    [Option("published-today", Required = false, Default = false, HelpText = "Filter to articles scraped today (from Today's Paper). Requires --audio-only.")]
    public bool PublishedToday { get; set; }

    [Option("podcast", Required = false, Default = false, HelpText = "Generate individual MP3s with RSS feed for podcasts (instead of M4B audiobook)")]
    public bool PodcastMode { get; set; }

    [Option("browse", Required = false, Default = false, HelpText = "Launch terminal browser mode for interactive web browsing")]
    public bool BrowseMode { get; set; }

    [Option("browse-url", Required = false, HelpText = "Initial URL to load in browse mode")]
    public string? BrowseUrl { get; set; }

    /// <summary>
    /// Validates the command options and returns validation errors if any
    /// </summary>
    /// <returns>List of validation error messages, empty if valid</returns>
    public List<string> Validate()
    {
        var errors = new List<string>();

        // Validate ArticleUrl if provided
        if (!string.IsNullOrWhiteSpace(ArticleUrl))
        {
            if (!Uri.TryCreate(ArticleUrl, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                errors.Add($"Invalid article URL: '{ArticleUrl}'. Must be a valid HTTP/HTTPS URL.");
            }
            else if (!ArticleUrl.Contains("nytimes.com", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"Article URL must be from nytimes.com domain. Got: '{ArticleUrl}'");
            }
        }

        // Validate ArticleCount
        if (ArticleCount <= 0)
        {
            errors.Add($"Article count must be positive. Got: {ArticleCount}");
        }

        if (ArticleCount > 1000)
        {
            errors.Add($"Article count cannot exceed 1000. Got: {ArticleCount}");
        }

        // Validate Budget
        if (Budget < 0)
        {
            errors.Add($"Budget cannot be negative. Got: {Budget:C}");
        }

        if (Budget > 1000)
        {
            errors.Add($"Budget cannot exceed $1000. Got: {Budget:C}");
        }

        // Validate OutputPath if provided
        if (!string.IsNullOrWhiteSpace(OutputPath))
        {
            try
            {
                var fullPath = Path.GetFullPath(OutputPath);

                // Check for invalid path characters
                if (OutputPath.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
                {
                    errors.Add($"Output path contains invalid characters: '{OutputPath}'");
                }

                // Validate not a root directory
                if (Path.GetPathRoot(fullPath) == fullPath)
                {
                    errors.Add($"Output path cannot be a root directory: '{OutputPath}'");
                }
            }
            catch (Exception ex)
            {
                errors.Add($"Invalid output path '{OutputPath}': {ex.Message}");
            }
        }

        // Validate VoiceId if provided (basic validation)
        if (!string.IsNullOrWhiteSpace(VoiceId))
        {
            if (VoiceId.Length > 100 || VoiceId.Any(c => !char.IsLetterOrDigit(c) && c != '-' && c != '_'))
            {
                errors.Add($"Invalid voice ID format: '{VoiceId}'. Must contain only alphanumeric characters, hyphens, and underscores.");
            }
        }

        // Validate ImportCookiesPath if provided
        if (!string.IsNullOrWhiteSpace(ImportCookiesPath))
        {
            if (!File.Exists(ImportCookiesPath))
            {
                errors.Add($"Cookie file not found: '{ImportCookiesPath}'");
            }
            else if (!ImportCookiesPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"Cookie file must be a JSON file (.json): '{ImportCookiesPath}'");
            }
        }

        // Validate conflicting options
        if (ScrapeOnly && TestMode)
        {
            errors.Add("Cannot use --scrape-only with --test. Test mode already skips audio generation.");
        }

        if (AudioOnly && ScrapeOnly)
        {
            errors.Add("Cannot use --audio-only with --scrape-only. These options are mutually exclusive.");
        }

        if (AudioOnly && !string.IsNullOrWhiteSpace(ArticleUrl))
        {
            errors.Add("Cannot use --audio-only with --url. Use --url for single article scraping.");
        }

        if (PublishedToday && !AudioOnly)
        {
            errors.Add("--published-today requires --audio-only. Use: --audio-only --published-today");
        }

        if (PodcastMode && ScrapeOnly)
        {
            errors.Add("Cannot use --podcast with --scrape-only. Podcast mode requires audio generation.");
        }

        // Validate browse mode
        if (BrowseMode)
        {
            // Browse mode is exclusive - cannot combine with scraping or audio options
            if (!string.IsNullOrWhiteSpace(ArticleUrl))
            {
                errors.Add("Cannot use --browse with --url. Browse mode is a standalone feature.");
            }

            if (AudioOnly)
            {
                errors.Add("Cannot use --browse with --audio-only. Browse mode is a standalone feature.");
            }

            if (ScrapeOnly)
            {
                errors.Add("Cannot use --browse with --scrape-only. Browse mode is a standalone feature.");
            }

            if (PodcastMode)
            {
                errors.Add("Cannot use --browse with --podcast. Browse mode is a standalone feature.");
            }
        }

        // Validate browse-url requires browse mode
        if (!string.IsNullOrWhiteSpace(BrowseUrl) && !BrowseMode)
        {
            errors.Add("--browse-url requires --browse. Use: --browse --browse-url <url>");
        }

        // Validate browse-url format
        if (!string.IsNullOrWhiteSpace(BrowseUrl))
        {
            if (!Uri.TryCreate(BrowseUrl, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                errors.Add($"Invalid browse URL: '{BrowseUrl}'. Must be a valid HTTP/HTTPS URL.");
            }
        }

        return errors;
    }
}
