// <copyright file="CookieManagerTests.cs" company="TermReader">
// Educational and personal use only.
// </copyright>


using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TermReader.Application.Interfaces;
using TermReader.Infrastructure.Browser;
using TermReader.Infrastructure.Browser.Models;
using TermReader.Infrastructure.Security;
using Xunit;

namespace TermReader.Tests;

public class CookieManagerTests : IDisposable
{
    private readonly CookieManager _cookieManager;
    private readonly ICookieEncryptionService _encryptionService;
    private readonly ILogger<CookieManager> _logger;
    private readonly string _testCookiePath;
    private readonly string _testDirectory;

    public CookieManagerTests()
    {
        _logger = Substitute.For<ILogger<CookieManager>>();

        // Setup Data Protection Provider for testing
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddDataProtection();
        var serviceProvider = serviceCollection.BuildServiceProvider();
        var dataProtectionProvider = serviceProvider.GetRequiredService<IDataProtectionProvider>();

        var encryptionLogger = Substitute.For<ILogger<DpapiCookieEncryptionService>>();
        _encryptionService = new DpapiCookieEncryptionService(dataProtectionProvider, encryptionLogger);

        _cookieManager = new CookieManager(_logger, _encryptionService);

        // Use a test directory for cookies
        _testDirectory = Path.Combine(Path.GetTempPath(), "TermReaderTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDirectory);
        _testCookiePath = Path.Combine(_testDirectory, "cookies.json");

        // Use reflection to set the cookie file path for testing
        var field = typeof(CookieManager).GetField("_cookieFilePath",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        field?.SetValue(_cookieManager, _testCookiePath);
    }

    public void Dispose()
    {
        // Cleanup test directory
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, true);
        }
    }

    [Fact]
    public async Task GetCookieInfoAsync_NoCookieFile_ReturnsInfoWithExistsFalse()
    {
        // Act
        var info = await _cookieManager.GetCookieInfoAsync();

        // Assert
        info.Should().NotBeNull();
        info!.Exists.Should().BeFalse();
        info.FilePath.Should().Be(_testCookiePath);
    }

    [Fact]
    public async Task GetCookieInfoAsync_V2EncryptedCookies_ReturnsCorrectInfo()
    {
        // Arrange
        var cookies = new List<CookieData>
        {
            new() { Name = "session", Value = "abc123", Domain = ".nytimes.com", Path = "/" },
            new() { Name = "auth", Value = "xyz789", Domain = ".nytimes.com", Path = "/" }
        };

        var cookieContainer = new CookieDataContainer
        {
            Cookies = cookies,
            Metadata = new Dictionary<string, string>
            {
                ["user_agent"] = "TestAgent"
            }
        };

        var json = JsonSerializer.Serialize(cookieContainer);
        var encrypted = _encryptionService.Encrypt(json);

        var storage = new CookieStorage
        {
            Version = 2,
            CreatedAt = DateTime.UtcNow.AddDays(-5),
            ExpiresAt = DateTime.UtcNow.AddDays(25),
            EncryptedData = encrypted
        };

        await File.WriteAllTextAsync(_testCookiePath,
            JsonSerializer.Serialize(storage, new JsonSerializerOptions { WriteIndented = true }));

        // Act
        var info = await _cookieManager.GetCookieInfoAsync();

        // Assert
        info.Should().NotBeNull();
        info!.Exists.Should().BeTrue();
        info.Version.Should().Be(2);
        info.IsEncrypted.Should().BeTrue();
        info.IsExpired.Should().BeFalse();
        info.CookieCount.Should().Be(2);
        info.ExpiresAt.Should().BeCloseTo(DateTime.UtcNow.AddDays(25), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task GetCookieInfoAsync_V1PlainTextCookies_ReturnsCorrectInfo()
    {
        // Arrange
        var cookies = new List<CookieData>
        {
            new() { Name = "session", Value = "abc123", Domain = ".nytimes.com", Path = "/" },
            new() { Name = "auth", Value = "xyz789", Domain = ".nytimes.com", Path = "/" }
        };

        await File.WriteAllTextAsync(_testCookiePath,
            JsonSerializer.Serialize(cookies, new JsonSerializerOptions { WriteIndented = true }));

        // Act
        var info = await _cookieManager.GetCookieInfoAsync();

        // Assert
        info.Should().NotBeNull();
        info!.Exists.Should().BeTrue();
        info.Version.Should().Be(1);
        info.IsEncrypted.Should().BeFalse();
        info.CookieCount.Should().Be(2);
    }

    [Fact]
    public async Task GetCookieInfoAsync_ExpiredCookies_ReturnsExpiredTrue()
    {
        // Arrange
        var cookies = new List<CookieData>
        {
            new() { Name = "session", Value = "abc123", Domain = ".nytimes.com", Path = "/" }
        };

        var cookieContainer = new CookieDataContainer
        {
            Cookies = cookies,
            Metadata = new Dictionary<string, string>()
        };

        var json = JsonSerializer.Serialize(cookieContainer);
        var encrypted = _encryptionService.Encrypt(json);

        var storage = new CookieStorage
        {
            Version = 2,
            CreatedAt = DateTime.UtcNow.AddDays(-35),
            ExpiresAt = DateTime.UtcNow.AddDays(-5), // Expired 5 days ago
            EncryptedData = encrypted
        };

        await File.WriteAllTextAsync(_testCookiePath,
            JsonSerializer.Serialize(storage, new JsonSerializerOptions { WriteIndented = true }));

        // Act
        var info = await _cookieManager.GetCookieInfoAsync();

        // Assert
        info.Should().NotBeNull();
        info!.Exists.Should().BeTrue();
        info.IsExpired.Should().BeTrue();
        info.ExpiresAt.Should().BeBefore(DateTime.UtcNow);
    }

    [Fact]
    public async Task ClearCookiesAsync_CookieFileExists_DeletesFileAndReturnsTrue()
    {
        // Arrange
        var storage = new CookieStorage
        {
            Version = 2,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(30),
            EncryptedData = new byte[] { 1, 2, 3 }
        };

        await File.WriteAllTextAsync(_testCookiePath, JsonSerializer.Serialize(storage));

        // Act
        var result = await _cookieManager.ClearCookiesAsync();

        // Assert
        result.Should().BeTrue();
        File.Exists(_testCookiePath).Should().BeFalse();
    }

    [Fact]
    public async Task ClearCookiesAsync_NoCookieFile_ReturnsFalse()
    {
        // Act
        var result = await _cookieManager.ClearCookiesAsync();

        // Assert
        result.Should().BeFalse();
        File.Exists(_testCookiePath).Should().BeFalse();
    }

    [Fact]
    public async Task GetCookieInfoAsync_CorruptedEncryptedData_ReturnsInfoWithNullCookieCount()
    {
        // Arrange
        var storage = new CookieStorage
        {
            Version = 2,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(30),
            EncryptedData = new byte[] { 1, 2, 3, 4, 5 } // Invalid encrypted data
        };

        await File.WriteAllTextAsync(_testCookiePath, JsonSerializer.Serialize(storage));

        // Act
        var info = await _cookieManager.GetCookieInfoAsync();

        // Assert
        info.Should().NotBeNull();
        info!.Exists.Should().BeTrue();
        info.Version.Should().Be(2);
        info.CookieCount.Should().BeNull("corrupted data should not be decryptable");
    }

    [Fact]
    public async Task GetCookieInfoAsync_InvalidJsonFormat_ReturnsNull()
    {
        // Arrange
        await File.WriteAllTextAsync(_testCookiePath, "{ invalid json }");

        // Act
        var info = await _cookieManager.GetCookieInfoAsync();

        // Assert
        info.Should().BeNull("invalid JSON should return null");
    }
}
