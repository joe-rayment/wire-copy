// <copyright file="DpapiCookieEncryptionService.cs" company="NYTAudioScraper">
// Copyright (c) NYTAudioScraper. All rights reserved.
// </copyright>

using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using NYTAudioScraper.Application.Interfaces;
using System.Text;

namespace NYTAudioScraper.Infrastructure.Security;

/// <summary>
/// Cookie encryption service using ASP.NET Core Data Protection API
/// This provides cross-platform encryption support (Windows, Linux, macOS)
/// </summary>
public class DpapiCookieEncryptionService : ICookieEncryptionService
{
    private readonly IDataProtector _protector;
    private readonly ILogger<DpapiCookieEncryptionService> _logger;
    private const string Purpose = "NYTAudioScraper.CookieProtection";

    public DpapiCookieEncryptionService(
        IDataProtectionProvider dataProtectionProvider,
        ILogger<DpapiCookieEncryptionService> logger)
    {
        _protector = dataProtectionProvider.CreateProtector(Purpose);
        _logger = logger;
    }

    public byte[] Encrypt(string plainText)
    {
        if (string.IsNullOrEmpty(plainText))
        {
            throw new ArgumentException("Plain text cannot be null or empty", nameof(plainText));
        }

        try
        {
            var plainBytes = Encoding.UTF8.GetBytes(plainText);
            var protectedText = _protector.Protect(Encoding.UTF8.GetString(plainBytes));
            var protectedBytes = Encoding.UTF8.GetBytes(protectedText);

            _logger.LogDebug("Successfully encrypted {Length} bytes of data", plainBytes.Length);
            return protectedBytes;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to encrypt data");
            throw;
        }
    }

    public string Decrypt(byte[] cipherText)
    {
        if (cipherText == null || cipherText.Length == 0)
        {
            throw new ArgumentException("Cipher text cannot be null or empty", nameof(cipherText));
        }

        try
        {
            var protectedText = Encoding.UTF8.GetString(cipherText);
            var unprotectedText = _protector.Unprotect(protectedText);

            _logger.LogDebug("Successfully decrypted {Length} bytes of data", cipherText.Length);
            return unprotectedText;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to decrypt data - data may be corrupted or encrypted with different key");
            throw;
        }
    }
}
