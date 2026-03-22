// Educational and personal use only.

using System.Runtime.InteropServices;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using TermReader.Application.DTOs.Browser;
using TermReader.Application.Interfaces;
using TermReader.Infrastructure.Browser;
using TermReader.Infrastructure.Configuration;
using Xunit;

namespace TermReader.Tests.Browser;

/// <summary>
/// Tests that BrowserSession correctly detects ARM64 Linux and reports
/// Selenium availability so callers get a clear error instead of a
/// cryptic chromedriver crash.
/// </summary>
[Trait("Category", "Unit")]
public class BrowserSessionArm64Tests
{
    private static BrowserSession CreateSession()
    {
        var logger = Substitute.For<ILogger<BrowserSession>>();
        var config = Options.Create(new BrowserConfiguration());
        var cookieManager = Substitute.For<ICookieManager>();
        cookieManager.LoadCookiesAsync().Returns(Task.FromResult<IReadOnlyList<StoredCookie>>([]));

        return new BrowserSession(config, logger, cookieManager);
    }

    [Fact]
    public void IsSeleniumAvailable_DoesNotThrow()
    {
        // Arrange
        using var session = CreateSession();

        // Act — property access must never throw regardless of platform
        var act = () => _ = session.IsSeleniumAvailable;

        // Assert
        act.Should().NotThrow();

        // On ARM64 Linux the Selenium-managed chromedriver is x86_64 and cannot run
        if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            && OperatingSystem.IsLinux())
        {
            session.IsSeleniumAvailable.Should().BeFalse(
                "Selenium Manager downloads x86_64 chromedriver which cannot execute on ARM64 Linux");
        }
    }

    [Fact]
    public void GetOrCreateDriver_WhenSeleniumUnavailable_ThrowsInvalidOperationWithClearMessage()
    {
        // This test is only meaningful on ARM64 Linux where Selenium is unavailable.
        // On other platforms we skip rather than produce a false negative.
        if (RuntimeInformation.ProcessArchitecture != Architecture.Arm64
            || !OperatingSystem.IsLinux())
        {
            // Not ARM64 Linux — nothing to assert; skip gracefully.
            return;
        }

        // Arrange
        using var session = CreateSession();

        // Act
        var act = () => session.GetOrCreateDriver(headless: true);

        // Assert — the message should mention both "Selenium" and "unavailable"
        act.Should().Throw<InvalidOperationException>()
            .And.Message.Should()
            .Contain("Selenium").And
            .Contain("unavailable");
    }
}
