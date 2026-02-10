// <copyright file="CookieInfo.cs" company="TermReader">
// Educational and personal use only.
// </copyright>


namespace TermReader.Application.Interfaces;

/// <summary>
/// Information about stored cookies
/// </summary>
public class CookieInfo
{
    /// <summary>
    /// Whether a cookie file exists
    /// </summary>
    public bool Exists { get; set; }

    /// <summary>
    /// When the cookies were created/saved
    /// </summary>
    public DateTime? CreatedAt { get; set; }

    /// <summary>
    /// When the cookies will expire
    /// </summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>
    /// Whether the cookies have expired
    /// </summary>
    public bool IsExpired { get; set; }

    /// <summary>
    /// Whether the cookies are encrypted (v2) or plain text (v1)
    /// </summary>
    public bool IsEncrypted { get; set; }

    /// <summary>
    /// Storage format version (1 = plain text, 2 = encrypted)
    /// </summary>
    public int Version { get; set; }

    /// <summary>
    /// Number of cookies stored
    /// </summary>
    public int? CookieCount { get; set; }

    /// <summary>
    /// Cookie file path
    /// </summary>
    public string? FilePath { get; set; }
}
