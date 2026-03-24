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

        // On ARM64 Linux, IsSeleniumAvailable depends on whether real (not snap stub)
        // Chrome and chromedriver binaries are found and pass --version check.
        if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            && OperatingSystem.IsLinux())
        {
            // The result depends on environment — just verify it doesn't throw.
            // Specific snap stub detection is tested below.
            _ = session.IsSeleniumAvailable;
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
    public void SnapStubs_AreRejectedOnArm64()
    {
        // Snap transitional stubs exist as files but exit non-zero.
        // On ARM64 Linux, if only snap stubs are available, IsSeleniumAvailable must be false.
        if (RuntimeInformation.ProcessArchitecture != Architecture.Arm64
            || !OperatingSystem.IsLinux())
        {
            return;
        }

        // Check if /usr/bin/chromedriver is a snap stub (exits non-zero with --version)
        var chromedriver = "/usr/bin/chromedriver";
        if (!File.Exists(chromedriver))
        {
            return; // No snap stub installed — can't test
        }

        using var process = new System.Diagnostics.Process();
        process.StartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = chromedriver,
            Arguments = "--version",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        try
        {
            process.Start();
            process.WaitForExit(5000);
        }
        catch
        {
            return; // Binary can't execute — not a snap stub scenario
        }

        if (process.ExitCode != 0)
        {
            // It's a snap stub — BrowserSession should detect this
            using var session = CreateSession();
            session.IsSeleniumAvailable.Should().BeFalse(
                "snap stub chromedriver exits non-zero and should be rejected");
        }
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
