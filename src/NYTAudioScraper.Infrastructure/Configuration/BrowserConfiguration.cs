// <copyright file="BrowserConfiguration.cs" company="NYT Audio Scraper">
// Educational and personal use only.
// </copyright>

namespace NYTAudioScraper.Infrastructure.Configuration;

public class BrowserConfiguration
{
    public const string SectionName = "Browser";

    public string BrowserType { get; init; } = "Chrome"; // "Chrome" or "Firefox" - Primary browser
    public string FallbackBrowserType { get; init; } = "Firefox"; // Fallback browser if primary is blocked
    public bool Headless { get; init; } = true;
    public int ImplicitWaitSeconds { get; init; } = 10;
    public int PageLoadTimeoutSeconds { get; init; } = 30;
    public bool DisableImages { get; init; } = true;
    public bool DisableJavaScript { get; init; } = false;
    public string UserAgent { get; init; } = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";
    public string[] ExperimentalOptions { get; init; } =
    [
        "excludeSwitches", "enable-automation",
        "useAutomationExtension", "false"
    ];
}
