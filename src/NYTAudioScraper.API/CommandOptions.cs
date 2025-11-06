using CommandLine;

namespace NYTAudioScraper.API;

public class CommandOptions
{
    [Option('u', "url", Required = false, HelpText = "Specific NYT article URL to process")]
    public string? ArticleUrl { get; set; }

    [Option('s', "section", Required = false, HelpText = "NYT section to scrape (e.g., technology, politics)")]
    public string? Section { get; set; }

    [Option('c', "count", Required = false, Default = 100, HelpText = "Number of articles to process (default: 100, collects all from specified sections)")]
    public int ArticleCount { get; set; }

    [Option('v', "voice", Required = false, HelpText = "ElevenLabs voice ID to use")]
    public string? VoiceId { get; set; }

    [Option('b', "budget", Required = false, HelpText = "Maximum budget in dollars (default: 5.0)")]
    public decimal Budget { get; set; } = 5.0m;

    [Option('o', "output", Required = false, HelpText = "Output directory path")]
    public string? OutputPath { get; set; }

    [Option("skip-login", Required = false, Default = false, HelpText = "Skip NYT login (only scrape public articles)")]
    public bool SkipLogin { get; set; }

    [Option("test", Required = false, Default = false, HelpText = "Run with test/mock data")]
    public bool TestMode { get; set; }

    [Option("cookie-info", Required = false, Default = false, HelpText = "Display information about stored cookies")]
    public bool CookieInfo { get; set; }

    [Option("clear-cookies", Required = false, Default = false, HelpText = "Clear all stored cookies")]
    public bool ClearCookies { get; set; }

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

        return errors;
    }
}
