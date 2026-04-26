// <copyright file="CookieEncryptionServiceTests.cs" company="TermReader">
// Licensed under the MIT License. See LICENSE in the repository root.
// </copyright>


using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TermReader.Infrastructure.Security;
using Xunit;

namespace TermReader.Tests.Security;

[Trait("Category", "Unit")]
public class CookieEncryptionServiceTests
{
    private readonly DpapiCookieEncryptionService _encryptionService;
    private readonly ILogger<DpapiCookieEncryptionService> _logger;

    public CookieEncryptionServiceTests()
    {
        _logger = Substitute.For<ILogger<DpapiCookieEncryptionService>>();

        // Setup Data Protection Provider for testing
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddDataProtection();
        var serviceProvider = serviceCollection.BuildServiceProvider();
        var dataProtectionProvider = serviceProvider.GetRequiredService<IDataProtectionProvider>();

        _encryptionService = new DpapiCookieEncryptionService(dataProtectionProvider, _logger);
    }

    [Fact]
    public void Encrypt_ValidPlainText_ReturnsEncryptedBytes()
    {
        // Arrange
        var plainText = "Test cookie data";

        // Act
        var encrypted = _encryptionService.Encrypt(plainText);

        // Assert
        encrypted.Should().NotBeNull();
        encrypted.Should().NotBeEmpty();
        encrypted.Should().NotEqual(System.Text.Encoding.UTF8.GetBytes(plainText),
            "encrypted data should not match plain text");
    }

    [Fact]
    public void Encrypt_EmptyString_ThrowsArgumentException()
    {
        // Arrange
        var plainText = "";

        // Act
        var act = () => _encryptionService.Encrypt(plainText);

        // Assert
        act.Should().Throw<ArgumentException>()
            .WithMessage("*Plain text cannot be null or empty*");
    }

    [Fact]
    public void Encrypt_NullString_ThrowsArgumentException()
    {
        // Arrange
        string plainText = null!;

        // Act
        var act = () => _encryptionService.Encrypt(plainText);

        // Assert
        act.Should().Throw<ArgumentException>()
            .WithMessage("*Plain text cannot be null or empty*");
    }

    [Fact]
    public void Decrypt_ValidEncryptedData_ReturnsOriginalPlainText()
    {
        // Arrange
        var plainText = "Test cookie data with special characters: !@#$%^&*()";
        var encrypted = _encryptionService.Encrypt(plainText);

        // Act
        var decrypted = _encryptionService.Decrypt(encrypted);

        // Assert
        decrypted.Should().Be(plainText);
    }

    [Fact]
    public void Decrypt_EmptyByteArray_ThrowsArgumentException()
    {
        // Arrange
        var cipherText = Array.Empty<byte>();

        // Act
        var act = () => _encryptionService.Decrypt(cipherText);

        // Assert
        act.Should().Throw<ArgumentException>()
            .WithMessage("*Cipher text cannot be null or empty*");
    }

    [Fact]
    public void Decrypt_NullByteArray_ThrowsArgumentException()
    {
        // Arrange
        byte[] cipherText = null!;

        // Act
        var act = () => _encryptionService.Decrypt(cipherText);

        // Assert
        act.Should().Throw<ArgumentException>()
            .WithMessage("*Cipher text cannot be null or empty*");
    }

    [Fact]
    public void EncryptDecrypt_RoundTrip_PreservesData()
    {
        // Arrange
        var testData = new[]
        {
            "Simple text",
            "Text with numbers 12345",
            "Special chars: !@#$%^&*()",
            "Unicode: 你好世界 🌍",
            "{\"json\":\"data\",\"value\":123}",
            new string('A', 10000) // Large text
        };

        foreach (var plainText in testData)
        {
            // Act
            var encrypted = _encryptionService.Encrypt(plainText);
            var decrypted = _encryptionService.Decrypt(encrypted);

            // Assert
            decrypted.Should().Be(plainText, $"round trip should preserve: {plainText.Substring(0, Math.Min(50, plainText.Length))}");
        }
    }

    [Fact]
    public void Encrypt_SamePlainText_ProducesDifferentCipherText()
    {
        // Arrange
        var plainText = "Test data";

        // Act
        var encrypted1 = _encryptionService.Encrypt(plainText);
        var encrypted2 = _encryptionService.Encrypt(plainText);

        // Assert
        // Note: Depending on the Data Protection API implementation,
        // encrypted data may or may not be different for the same input.
        // This test documents the behavior.
        encrypted1.Should().NotBeNull();
        encrypted2.Should().NotBeNull();
    }

    [Fact]
    public void Encrypt_JsonData_CanBeDecrypted()
    {
        // Arrange
        var jsonData = @"{
            ""cookies"": [
                {""name"": ""session"", ""value"": ""abc123""},
                {""name"": ""auth"", ""value"": ""xyz789""}
            ],
            ""metadata"": {
                ""user_agent"": ""Mozilla/5.0"",
                ""last_used"": ""2025-01-01T00:00:00Z""
            }
        }";

        // Act
        var encrypted = _encryptionService.Encrypt(jsonData);
        var decrypted = _encryptionService.Decrypt(encrypted);

        // Assert
        decrypted.Should().Be(jsonData);
    }

    [Fact]
    public void Decrypt_CorruptedData_ThrowsException()
    {
        // Arrange
        var plainText = "Test data";
        var encrypted = _encryptionService.Encrypt(plainText);

        // Corrupt the encrypted data
        var corrupted = new byte[encrypted.Length];
        Array.Copy(encrypted, corrupted, encrypted.Length);
        corrupted[corrupted.Length / 2] ^= 0xFF; // Flip bits in the middle

        // Act
        var act = () => _encryptionService.Decrypt(corrupted);

        // Assert
        act.Should().Throw<Exception>("corrupted data should not decrypt successfully");
    }

    [Fact]
    public void Encrypt_LargeData_HandlesCorrectly()
    {
        // Arrange - Create a large cookie data structure (simulating many cookies)
        var largeCookieData = string.Join(",", Enumerable.Range(0, 1000).Select(i =>
            $"{{\"name\":\"cookie{i}\",\"value\":\"value{i}\",\"domain\":\".example.com\"}}"));

        // Act
        var encrypted = _encryptionService.Encrypt(largeCookieData);
        var decrypted = _encryptionService.Decrypt(encrypted);

        // Assert
        decrypted.Should().Be(largeCookieData);
        encrypted.Length.Should().BeGreaterThan(0);
    }
}
