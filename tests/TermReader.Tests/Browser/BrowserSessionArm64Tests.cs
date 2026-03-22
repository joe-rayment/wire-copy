// Educational and personal use only.

using System.Net;
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

        // On ARM64 Linux without CHROME_BIN/CHROMEDRIVER_PATH set to real binaries,
        // Selenium-managed chromedriver is x86_64 and cannot run.
        // If both env vars point to existing files, Selenium is considered available.
        if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            && OperatingSystem.IsLinux())
        {
            var chromeBin = Environment.GetEnvironmentVariable("CHROME_BIN");
            var driverPath = Environment.GetEnvironmentVariable("CHROMEDRIVER_PATH");
            var hasSystemBinaries = !string.IsNullOrEmpty(chromeBin) && File.Exists(chromeBin)
                && !string.IsNullOrEmpty(driverPath) && File.Exists(driverPath);

            if (hasSystemBinaries)
            {
                session.IsSeleniumAvailable.Should().BeTrue(
                    "CHROME_BIN and CHROMEDRIVER_PATH point to real ARM64 binaries");
            }
            else
            {
                session.IsSeleniumAvailable.Should().BeFalse(
                    "Selenium Manager downloads x86_64 chromedriver which cannot execute on ARM64 Linux");
            }
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

    [Fact]
    [Trait("Category", "Unit")]
    public async Task PageLoader_WhenSeleniumUnavailable_SkipsBrowserFetch()
    {
        // Arrange
        var browserSession = Substitute.For<IBrowserSession>();
        browserSession.IsSeleniumAvailable.Returns(false);

        var config = Options.Create(new BrowserConfiguration());
        var logger = Substitute.For<ILogger<PageLoader>>();

        // PageLoader constructor: (IOptions<BrowserConfiguration>, ILogger<PageLoader>, IBrowserSession, HttpClient?)
        var httpClient = new HttpClient(new FakeHttpHandler(
            "<html><head><title>Test Page</title></head><body>" +
            "<h1>Welcome</h1><p>Hello world, this is a simple page with enough content to not be flagged as empty.</p>" +
            "</body></html>"));
        var pageLoader = new PageLoader(config, logger, browserSession, httpClient);

        // Act
        var result = await pageLoader.LoadAsync(
            new PageLoadRequest { Url = "https://example.com" },
            CancellationToken.None);

        // Assert — loaded via HTTP, never called GetOrCreateDriver
        result.Success.Should().BeTrue();
        browserSession.DidNotReceive().GetOrCreateDriver(Arg.Any<bool>());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void SeleniumCacheCleanup_WhenNotArm64_DoesNotDeleteCache()
    {
        // On non-ARM64, ProbeSeleniumAvailability returns true immediately
        // and never calls CleanupX86SeleniumCache. Verify the cache dir
        // is left alone on x86_64.
        if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            && OperatingSystem.IsLinux())
        {
            return; // This test only validates non-ARM64 behavior
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var seleniumCacheDir = Path.Combine(home, ".cache", "selenium");

        // If cache exists before, it should still exist after session creation
        var existedBefore = Directory.Exists(seleniumCacheDir);

        using var session = CreateSession();

        if (existedBefore)
        {
            Directory.Exists(seleniumCacheDir).Should().BeTrue(
                "Selenium cache should not be deleted on non-ARM64");
        }
    }

    private class FakeHttpHandler : HttpMessageHandler
    {
        private readonly string _html;
        public FakeHttpHandler(string html) => _html = html;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_html, System.Text.Encoding.UTF8, "text/html"),
                RequestMessage = request,
            });
        }
    }
}
