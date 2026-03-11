// <copyright file="ICookieManager.cs" company="TermReader">
// Educational and personal use only.
// </copyright>


namespace TermReader.Application.Interfaces;

/// <summary>
/// Cookie data for injection into HTTP clients and WebDriver.
/// </summary>
public record StoredCookie(string Name, string Value, string Domain, string Path, DateTime? Expiry);

/// <summary>
/// Service for managing stored authentication cookies
/// </summary>
public interface ICookieManager
{
    /// <summary>
    /// Gets information about stored cookies
    /// </summary>
    /// <returns>Cookie information or null if no cookies exist</returns>
    Task<CookieInfo?> GetCookieInfoAsync();

    /// <summary>
    /// Loads all stored cookies, decrypting if necessary, and filtering out expired entries.
    /// </summary>
    /// <returns>A read-only list of stored cookies, or an empty list if none exist or an error occurs.</returns>
    Task<IReadOnlyList<StoredCookie>> LoadCookiesAsync();

    /// <summary>
    /// Clears all stored cookies
    /// </summary>
    /// <returns>True if cookies were cleared, false if no cookies existed</returns>
    Task<bool> ClearCookiesAsync();
}
