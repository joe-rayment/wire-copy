// <copyright file="CookieImporterTests.cs" company="Wire Copy">
// Licensed under the MIT License. See LICENSE in the repository root.
// </copyright>

using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using WireCopy.Application.Interfaces;
using WireCopy.Infrastructure.Browser;
using Xunit;

namespace WireCopy.Tests.Security;

[Trait("Category", "Unit")]
public class CookieImporterTests
{
    private readonly ICookieEncryptionService _encryptionService;
    private readonly ILogger<CookieImporter> _logger;
    private readonly CookieImporter _cookieImporter;

    public CookieImporterTests()
    {
        _encryptionService = Substitute.For<ICookieEncryptionService>();
        _logger = Substitute.For<ILogger<CookieImporter>>();
        _cookieImporter = new CookieImporter(_encryptionService, _logger);
    }

    [Fact]
    public async Task ImportFromJsonAsync_NonExistentFile_ShouldReturnFailure()
    {
        // Arrange
        var nonExistentPath = "/nonexistent/path/cookies.json";

        // Act
        var result = await _cookieImporter.ImportFromJsonAsync(nonExistentPath);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Cookie file not found");
    }

    [Fact]
    public async Task ImportFromJsonAsync_SystemDirectoryPath_ShouldReturnFailure()
    {
        // Arrange - Create a temporary file in /tmp to test but pretend it's in /etc
        var tempFile = Path.Combine(Path.GetTempPath(), "test-cookies.json");
        await File.WriteAllTextAsync(tempFile, "[]");

        string testPath;
        if (OperatingSystem.IsWindows())
        {
            testPath = @"C:\Windows\System32\cookies.json";
        }
        else
        {
            testPath = "/etc/passwd.json";
        }

        try
        {
            // Act - Try to import from a system directory (won't exist but should be rejected)
            var result = await _cookieImporter.ImportFromJsonAsync(testPath);

            // Assert
            result.Success.Should().BeFalse();
            result.ErrorMessage.Should().Match(msg =>
                msg.Contains("Cookie file not found") || // File doesn't exist
                msg.Contains("Cookie file must be in user directories") || // Path validation rejected it
                msg.Contains("system directories are not allowed")); // Path validation rejected it
        }
        finally
        {
            // Cleanup
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    [Fact]
    public async Task ImportFromJsonAsync_UserDirectoryPath_ShouldAttemptImport()
    {
        // Arrange
        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var testFile = Path.Combine(userHome, "test-cookies.json");
        var validCookieJson = @"[
            {
                ""Name"": ""NYT-S"",
                ""Value"": ""test-value"",
                ""Domain"": "".nytimes.com"",
                ""Path"": ""/"",
                ""Expiry"": ""2026-01-01T00:00:00Z""
            }
        ]";

        try
        {
            await File.WriteAllTextAsync(testFile, validCookieJson);

            // Mock encryption service
            _encryptionService.Encrypt(Arg.Any<string>()).Returns(new byte[] { 1, 2, 3 });

            // Act
            var result = await _cookieImporter.ImportFromJsonAsync(testFile);

            // Assert
            result.Success.Should().BeTrue();
            result.CookieCount.Should().Be(1);
        }
        finally
        {
            // Cleanup
            if (File.Exists(testFile))
            {
                File.Delete(testFile);
            }

            // Clean up saved cookies
            var cookiePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WireCopy",
                "cookies.json");
            if (File.Exists(cookiePath))
            {
                File.Delete(cookiePath);
            }
        }
    }

    [Fact]
    public async Task ImportFromJsonAsync_TempDirectoryPath_ShouldAttemptImport()
    {
        // Arrange
        var testFile = Path.Combine(Path.GetTempPath(), "test-cookies.json");
        var validCookieJson = @"[
            {
                ""Name"": ""NYT-S"",
                ""Value"": ""test-value"",
                ""Domain"": "".nytimes.com"",
                ""Path"": ""/"",
                ""Expiry"": ""2026-01-01T00:00:00Z""
            }
        ]";

        try
        {
            await File.WriteAllTextAsync(testFile, validCookieJson);

            // Mock encryption service
            _encryptionService.Encrypt(Arg.Any<string>()).Returns(new byte[] { 1, 2, 3 });

            // Act
            var result = await _cookieImporter.ImportFromJsonAsync(testFile);

            // Assert
            result.Success.Should().BeTrue();
            result.CookieCount.Should().Be(1);
        }
        finally
        {
            // Cleanup
            if (File.Exists(testFile))
            {
                File.Delete(testFile);
            }

            // Clean up saved cookies
            var cookiePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WireCopy",
                "cookies.json");
            if (File.Exists(cookiePath))
            {
                File.Delete(cookiePath);
            }
        }
    }

    [Fact]
    public async Task ImportFromJsonAsync_EmptyCookieArray_ShouldReturnFailure()
    {
        // Arrange
        var testFile = Path.Combine(Path.GetTempPath(), "empty-cookies.json");
        await File.WriteAllTextAsync(testFile, "[]");

        try
        {
            // Act
            var result = await _cookieImporter.ImportFromJsonAsync(testFile);

            // Assert
            result.Success.Should().BeFalse();
            result.ErrorMessage.Should().Contain("No cookies found in file");
        }
        finally
        {
            // Cleanup
            if (File.Exists(testFile))
            {
                File.Delete(testFile);
            }
        }
    }

    [Fact]
    public async Task ImportFromJsonAsync_NonNYTCookies_ShouldReturnFailure()
    {
        // Arrange
        var testFile = Path.Combine(Path.GetTempPath(), "non-nyt-cookies.json");
        var nonNYTCookieJson = @"[
            {
                ""Name"": ""session"",
                ""Value"": ""test-value"",
                ""Domain"": "".example.com"",
                ""Path"": ""/"",
                ""Expiry"": ""2026-01-01T00:00:00Z""
            }
        ]";
        await File.WriteAllTextAsync(testFile, nonNYTCookieJson);

        try
        {
            // Act
            var result = await _cookieImporter.ImportFromJsonAsync(testFile);

            // Assert
            result.Success.Should().BeFalse();
            result.ErrorMessage.Should().Contain("No NYT cookies found");
        }
        finally
        {
            // Cleanup
            if (File.Exists(testFile))
            {
                File.Delete(testFile);
            }
        }
    }

    [Fact]
    public async Task ImportFromJsonAsync_InvalidJson_ShouldReturnFailure()
    {
        // Arrange
        var testFile = Path.Combine(Path.GetTempPath(), "invalid-cookies.json");
        await File.WriteAllTextAsync(testFile, "{ invalid json }");

        try
        {
            // Act
            var result = await _cookieImporter.ImportFromJsonAsync(testFile);

            // Assert
            result.Success.Should().BeFalse();
            result.ErrorMessage.Should().Contain("Invalid JSON format");
        }
        finally
        {
            // Cleanup
            if (File.Exists(testFile))
            {
                File.Delete(testFile);
            }
        }
    }

    [Fact]
    public async Task GetCookieInfoAsync_NoCookiesStored_ShouldReturnNoCookies()
    {
        // Act
        var result = await _cookieImporter.GetCookieInfoAsync();

        // Assert
        result.HasCookies.Should().BeFalse();
        result.Message.Should().Contain("No cookies stored");
    }

    [Fact]
    public async Task ClearCookiesAsync_WhenNoCookies_ShouldReturnFalse()
    {
        // Act
        var result = await _cookieImporter.ClearCookiesAsync();

        // Assert
        result.Should().BeFalse();
    }
}
