// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces;
using WireCopy.Infrastructure.Browser;
using WireCopy.Infrastructure.Configuration;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// Tests that BrowserSession and PageLoader handle the disposed state gracefully:
/// - GetOrCreatePageAsync throws ObjectDisposedException after Dispose
/// - RestoreWindowAsync returns silently after Dispose (no exception)
/// - PageLoader.BrowserFetchAsync returns PageLoadResult.Failure when the session is disposed
/// </summary>
public class BrowserSessionDisposedTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetOrCreatePageAsync_AfterDispose_ThrowsObjectDisposedException()
    {
        // Arrange
        var logger = Substitute.For<ILogger<BrowserSession>>();
        var config = Options.Create(new BrowserConfiguration());
        var cookieManager = Substitute.For<ICookieManager>();
        cookieManager.LoadCookiesAsync().Returns(Task.FromResult<IReadOnlyList<StoredCookie>>([]));

        var session = new BrowserSession(config, logger, cookieManager);
        session.Dispose();

        // Act
        var act = async () => await session.GetOrCreatePageAsync(headless: true);

        // Assert
        await act.Should().ThrowAsync<ObjectDisposedException>()
            .Where(e => e.ObjectName!.Contains("BrowserSession"));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RestoreWindowAsync_AfterDispose_DoesNotThrow()
    {
        // Arrange
        var logger = Substitute.For<ILogger<BrowserSession>>();
        var config = Options.Create(new BrowserConfiguration());
        var cookieManager = Substitute.For<ICookieManager>();
        cookieManager.LoadCookiesAsync().Returns(Task.FromResult<IReadOnlyList<StoredCookie>>([]));

        var session = new BrowserSession(config, logger, cookieManager);
        session.Dispose();

        // Act & Assert - RestoreWindowAsync should return gracefully, not throw
        var act = async () => await session.RestoreWindowAsync();
        await act.Should().NotThrowAsync();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        // Arrange
        var logger = Substitute.For<ILogger<BrowserSession>>();
        var config = Options.Create(new BrowserConfiguration());
        var cookieManager = Substitute.For<ICookieManager>();
        cookieManager.LoadCookiesAsync().Returns(Task.FromResult<IReadOnlyList<StoredCookie>>([]));

        var session = new BrowserSession(config, logger, cookieManager);

        // Act & Assert - double dispose should be safe
        var act = () =>
        {
            session.Dispose();
            session.Dispose();
        };
        act.Should().NotThrow();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task PageLoader_BrowserFetch_WhenSessionDisposed_ReturnsFailure()
    {
        // Arrange - Mock IBrowserSession to throw ObjectDisposedException
        var browserSession = Substitute.For<IBrowserSession>();
        browserSession.IsBrowserAvailable.Returns(true);
        browserSession.GetOrCreatePageAsync(Arg.Any<bool>())
            .Returns<Microsoft.Playwright.IPage>(_ => throw new ObjectDisposedException("BrowserSession"));

        var logger = Substitute.For<ILogger<PageLoader>>();
        var config = Options.Create(new BrowserConfiguration());
        var pageLoader = new PageLoader(config, logger, browserSession, httpClient: null);

        var request = new PageLoadRequest { Url = "https://example.com" };

        // Act - Without httpClient, LoadAsync goes directly to BrowserFetchAsync
        var result = await pageLoader.LoadAsync(request);

        // Assert - Should return failure instead of throwing
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Browser session is no longer available");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task PageLoader_BrowserFetch_WhenSessionDisposed_AfterHttpFallback_ReturnsFailure()
    {
        // Arrange - HTTP fails (403), then browser session is disposed
        var browserSession = Substitute.For<IBrowserSession>();
        browserSession.IsBrowserAvailable.Returns(true);
        browserSession.GetOrCreatePageAsync(Arg.Any<bool>())
            .Returns<Microsoft.Playwright.IPage>(_ => throw new ObjectDisposedException("BrowserSession"));

        var logger = Substitute.For<ILogger<PageLoader>>();
        var config = Options.Create(new BrowserConfiguration());

        var handler = new FakeHttpMessageHandler(System.Net.HttpStatusCode.Forbidden, "Forbidden");
        var httpClient = new HttpClient(handler);
        var pageLoader = new PageLoader(config, logger, browserSession, httpClient);

        var request = new PageLoadRequest { Url = "https://example.com" };

        // Act - HTTP fails with 403, falls back to browser which is disposed
        var result = await pageLoader.LoadAsync(request);

        // Assert - Should gracefully return failure
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Browser session is no longer available");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task PageLoader_PreferBrowser_WhenSessionDisposed_FallsBackToHttp()
    {
        // Arrange - Browser disposed, but HTTP should still work as fallback
        var browserSession = Substitute.For<IBrowserSession>();
        browserSession.IsBrowserAvailable.Returns(true);
        browserSession.GetOrCreatePageAsync(Arg.Any<bool>())
            .Returns<Microsoft.Playwright.IPage>(_ => throw new ObjectDisposedException("BrowserSession"));

        var logger = Substitute.For<ILogger<PageLoader>>();
        var config = Options.Create(new BrowserConfiguration());

        var httpHtml = "<html><head><title>HTTP Fallback</title></head><body><p>Content via HTTP</p></body></html>";
        var handler = new FakeHttpMessageHandler(System.Net.HttpStatusCode.OK, httpHtml);
        var httpClient = new HttpClient(handler);
        var pageLoader = new PageLoader(config, logger, browserSession, httpClient);

        var request = new PageLoadRequest { Url = "https://example.com", PreferBrowser = true };

        // Act - PreferBrowser tries browser first (disposed), should fall back to HTTP
        var result = await pageLoader.LoadAsync(request);

        // Assert - HTTP fallback should succeed
        result.Success.Should().BeTrue();
        result.Html.Should().Be(httpHtml);
        result.Metadata!.Title.Should().Be("HTTP Fallback");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SyncCookiesToPreloadContextAsync_PreloadNotLaunched_ReturnsZero()
    {
        // workspace-8t9k Phase 2: when the preload context has not yet been
        // launched (no preload has run), the sync method must be a graceful
        // no-op — the cookies will be picked up from cookies.json on the next
        // launch via InjectStoredCookiesAsync.
        var logger = Substitute.For<ILogger<BrowserSession>>();
        var config = Options.Create(new BrowserConfiguration());
        var cookieManager = Substitute.For<ICookieManager>();
        cookieManager.LoadCookiesAsync().Returns(Task.FromResult<IReadOnlyList<StoredCookie>>([]));

        var session = new BrowserSession(config, logger, cookieManager);

        // Act — preload context is lazy, never launched in this test
        var pushed = await session.SyncCookiesToPreloadContextAsync(new[]
        {
            new StoredCookie("nyt-s", "abc", ".nytimes.com", "/", DateTime.UtcNow.AddDays(30)),
        });

        // Assert
        pushed.Should().Be(0, "preload context is lazy — sync must not launch it just to push");

        await session.DisposeAsync();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SyncCookiesToPreloadContextAsync_EmptyList_ReturnsZero()
    {
        var logger = Substitute.For<ILogger<BrowserSession>>();
        var config = Options.Create(new BrowserConfiguration());
        var cookieManager = Substitute.For<ICookieManager>();
        cookieManager.LoadCookiesAsync().Returns(Task.FromResult<IReadOnlyList<StoredCookie>>([]));

        var session = new BrowserSession(config, logger, cookieManager);

        var pushed = await session.SyncCookiesToPreloadContextAsync(Array.Empty<StoredCookie>());

        pushed.Should().Be(0);

        await session.DisposeAsync();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SyncCookiesToPreloadContextAsync_AfterDispose_ReturnsZero()
    {
        var logger = Substitute.For<ILogger<BrowserSession>>();
        var config = Options.Create(new BrowserConfiguration());
        var cookieManager = Substitute.For<ICookieManager>();
        cookieManager.LoadCookiesAsync().Returns(Task.FromResult<IReadOnlyList<StoredCookie>>([]));

        var session = new BrowserSession(config, logger, cookieManager);
        await session.DisposeAsync();

        var pushed = await session.SyncCookiesToPreloadContextAsync(new[]
        {
            new StoredCookie("nyt-s", "abc", ".nytimes.com", "/", DateTime.UtcNow.AddDays(30)),
        });

        pushed.Should().Be(0, "disposed session must not attempt cookie sync");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CreateBackgroundPageAsync_AfterDispose_ReturnsNull()
    {
        // workspace-8t9k Phase 2: background pages live in the preload context.
        // After disposal, requests for a background page must return null
        // without attempting to launch a new context.
        var logger = Substitute.For<ILogger<BrowserSession>>();
        var config = Options.Create(new BrowserConfiguration());
        var cookieManager = Substitute.For<ICookieManager>();
        cookieManager.LoadCookiesAsync().Returns(Task.FromResult<IReadOnlyList<StoredCookie>>([]));

        var session = new BrowserSession(config, logger, cookieManager);
        await session.DisposeAsync();

        var page = await session.CreateBackgroundPageAsync();

        page.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void WarmUpAsync_AfterDispose_ThrowsObjectDisposedException()
    {
        // Arrange - WarmUpAsync calls GetOrCreatePageAsync internally
        var logger = Substitute.For<ILogger<BrowserSession>>();
        var config = Options.Create(new BrowserConfiguration());
        var cookieManager = Substitute.For<ICookieManager>();
        cookieManager.LoadCookiesAsync().Returns(Task.FromResult<IReadOnlyList<StoredCookie>>([]));

        var session = new BrowserSession(config, logger, cookieManager);
        session.Dispose();

        // Act & Assert - WarmUpAsync delegates to GetOrCreatePageAsync which should throw
        var act = async () => await session.WarmUpAsync();
        act.Should().ThrowAsync<ObjectDisposedException>();
    }

    /// <summary>
    /// Fake HttpMessageHandler for testing HttpClient-based code without real HTTP calls.
    /// </summary>
    private class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly System.Net.HttpStatusCode _statusCode;
        private readonly string _content;

        public FakeHttpMessageHandler(System.Net.HttpStatusCode statusCode, string content)
        {
            _statusCode = statusCode;
            _content = content;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var response = new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_content),
                RequestMessage = request
            };

            return Task.FromResult(response);
        }
    }
}
