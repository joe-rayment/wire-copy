// <copyright file="CookieStorage.cs" company="NYTAudioScraper">
// Copyright (c) NYTAudioScraper. All rights reserved.
// </copyright>

namespace NYTAudioScraper.Infrastructure.Browser.Models;

/// <summary>
/// Represents the cookie storage format with encryption support
/// Version 1: Plain text cookies (legacy)
/// Version 2: Encrypted cookies (current)
/// </summary>
public class CookieStorage
{
    /// <summary>
    /// Storage format version
    /// Version 1: Plain text (List of CookieData)
    /// Version 2: Encrypted (EncryptedData byte array)
    /// </summary>
    public int Version { get; set; } = 2;

    /// <summary>
    /// When the cookies were saved
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// When the cookies expire and should be re-authenticated
    /// Default: 30 days from creation
    /// </summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>
    /// Encrypted cookie data (Version 2 only)
    /// Contains JSON-serialized CookieData
    /// </summary>
    public byte[]? EncryptedData { get; set; }

    /// <summary>
    /// Plain text cookies (Version 1 only - for migration)
    /// </summary>
    public List<CookieData>? Cookies { get; set; }
}
