// Educational and personal use only.

using OpenQA.Selenium;

namespace TermReader.Infrastructure.Browser;

/// <summary>
/// Service for authenticating with New York Times website.
/// </summary>
public interface IAuthService
{
    /// <summary>
    /// Authenticates with NYT using configured credentials or saved cookies.
    /// </summary>
    /// <param name="driver">The WebDriver instance to authenticate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if authentication was successful, false otherwise.</returns>
    Task<bool> AuthenticateAsync(IWebDriver driver, CancellationToken cancellationToken = default);
}
