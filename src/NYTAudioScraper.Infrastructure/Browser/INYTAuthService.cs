// <copyright file="INYTAuthService.cs" company="NYT Audio Scraper">
// Educational and personal use only.
// </copyright>

using OpenQA.Selenium;

namespace NYTAudioScraper.Infrastructure.Browser;

/// <summary>
/// Service for authenticating with New York Times website
/// </summary>
public interface INYTAuthService
{
    /// <summary>
    /// Authenticates with NYT using configured credentials or saved cookies
    /// </summary>
    /// <param name="driver">The WebDriver instance to authenticate</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if authentication was successful, false otherwise</returns>
    Task<bool> AuthenticateAsync(IWebDriver driver, CancellationToken cancellationToken = default);
}
