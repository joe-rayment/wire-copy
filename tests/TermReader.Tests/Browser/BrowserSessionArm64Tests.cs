// Educational and personal use only.

using System.Net;
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
/// Tests that BrowserSession correctly reports browser availability.
/// With Playwright, IsBrowserAvailable is always true (no platform-specific binary issues).
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
    public void IsBrowserAvailable_AlwaysReturnsTrue()
    {
        // Arrange
        using var session = CreateSession();

        // Act & Assert - Playwright is always available (no platform binary issues)
        session.IsBrowserAvailable.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task PageLoader_WhenBrowserUnavailable_SkipsBrowserFetch()
    {
        // Arrange
        var browserSession = Substitute.For<IBrowserSession>();
        browserSession.IsBrowserAvailable.Returns(false);

        var config = Options.Create(new BrowserConfiguration());
        var logger = Substitute.For<ILogger<PageLoader>>();

        var httpClient = new HttpClient(new FakeHttpHandler(
            "<html><head><title>Test Page</title></head><body>" +
            "<h1>Welcome</h1><p>Hello world, this is a simple page with enough content to not be flagged as empty.</p>" +
            "</body></html>"));
        var pageLoader = new PageLoader(config, logger, browserSession, httpClient);

        // Act
        var result = await pageLoader.LoadAsync(
            new PageLoadRequest { Url = "https://example.com" },
            CancellationToken.None);

        // Assert - loaded via HTTP, never called GetOrCreatePageAsync
        result.Success.Should().BeTrue();
        await browserSession.DidNotReceive().GetOrCreatePageAsync(Arg.Any<bool>());
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
