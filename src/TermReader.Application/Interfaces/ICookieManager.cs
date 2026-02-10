// <copyright file="ICookieManager.cs" company="TermReader">
// Educational and personal use only.
// </copyright>


namespace TermReader.Application.Interfaces;

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
    /// Clears all stored cookies
    /// </summary>
    /// <returns>True if cookies were cleared, false if no cookies existed</returns>
    Task<bool> ClearCookiesAsync();
}
