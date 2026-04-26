// Licensed under the MIT License. See LICENSE in the repository root.

namespace TermReader.Application.Interfaces.Browser;

/// <summary>
/// Automates login to paywalled sites using stored credentials.
/// </summary>
public interface IAutoLoginService
{
    /// <summary>
    /// Attempts to log in to a site using stored credentials.
    /// </summary>
    /// <param name="domain">The domain to log in to (e.g., "nytimes.com").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The login result.</returns>
    Task<AutoLoginResult> LoginAsync(string domain, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks whether stored credentials exist for the given domain.
    /// </summary>
    /// <param name="domain">The domain to check.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if credentials exist for the domain.</returns>
    Task<bool> HasCredentialsAsync(string domain, CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of an auto-login attempt.
/// </summary>
public sealed class AutoLoginResult
{
    /// <summary>
    /// Gets a value indicating whether the login was successful.
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// Gets an error message describing why the login failed, if applicable.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Gets a value indicating whether the browser was opened for manual login
    /// because automated login could not be completed.
    /// </summary>
    public bool ManualLoginRequired { get; init; }

    /// <summary>
    /// Creates a successful login result.
    /// </summary>
    public static AutoLoginResult Succeeded() => new() { Success = true };

    /// <summary>
    /// Creates a failed login result with the given error message.
    /// </summary>
    public static AutoLoginResult Failed(string error) => new() { ErrorMessage = error };

    /// <summary>
    /// Creates a result indicating the browser was opened for manual login.
    /// </summary>
    public static AutoLoginResult RequiresManualLogin(string reason) =>
        new() { ManualLoginRequired = true, ErrorMessage = reason };
}
