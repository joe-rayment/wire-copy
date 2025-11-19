// <copyright file="ICookieEncryptionService.cs" company="NYTAudioScraper">
// Copyright (c) NYTAudioScraper. All rights reserved.
// </copyright>

namespace NYTAudioScraper.Application.Interfaces;

/// <summary>
/// Service for encrypting and decrypting sensitive cookie data
/// </summary>
public interface ICookieEncryptionService
{
    /// <summary>
    /// Encrypts plain text data
    /// </summary>
    /// <param name="plainText">The plain text to encrypt</param>
    /// <returns>Encrypted data as byte array</returns>
    byte[] Encrypt(string plainText);

    /// <summary>
    /// Decrypts encrypted data back to plain text
    /// </summary>
    /// <param name="cipherText">The encrypted data</param>
    /// <returns>Decrypted plain text</returns>
    string Decrypt(byte[] cipherText);
}
