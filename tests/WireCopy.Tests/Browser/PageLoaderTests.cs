// Licensed under the MIT License. See LICENSE in the repository root.

using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using NSubstitute;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Infrastructure.Browser;
using WireCopy.Infrastructure.Configuration;
using Xunit;

namespace WireCopy.Tests.Browser;

[Trait("Category", "Unit")]
public class PageLoaderTests
{
    private readonly ILogger<PageLoader> _logger;
    private readonly IOptions<BrowserConfiguration> _browserConfig;
    private readonly IBrowserSession _browserSession;

    public PageLoaderTests()
    {
        _logger = Substitute.For<ILogger<PageLoader>>();
        _browserConfig = Options.Create(new BrowserConfiguration());
        _browserSession = Substitute.For<IBrowserSession>();
        _browserSession.IsBrowserAvailable.Returns(true);
    }

    private PageLoader CreateSut(HttpClient? httpClient = null, BrowserConfiguration? config = null)
    {
        var options = config != null ? Options.Create(config) : _browserConfig;
        return new PageLoader(options, _logger, _browserSession, httpClient);
    }

    private static HttpClient CreateMockHttpClient(HttpStatusCode statusCode, string content)
    {
        var handler = new FakeHttpMessageHandler(statusCode, content);
        return new HttpClient(handler);
    }

    [Fact]
    public async Task LoadAsync_WithHttpClient_SuccessfulHtmlResponse_ReturnsSuccess()
    {
        // Arrange
        var html = "<html><head><title>Test Page</title></head><body><p>Hello</p></body></html>";
        var httpClient = CreateMockHttpClient(HttpStatusCode.OK, html);
        var sut = CreateSut(httpClient);
        var request = new PageLoadRequest { Url = "https://example.com" };

        // Act
        var result = await sut.LoadAsync(request);

        // Assert
        result.Success.Should().BeTrue();
        result.Html.Should().Be(html);
        result.Url.Should().Match(u =>
            u == "https://example.com" || u == "https://example.com/",
            "URL should be the requested domain (HttpClient may normalize trailing slash)");
        result.Metadata.Should().NotBeNull();
        result.Metadata!.Title.Should().Be("Test Page");
    }

    [Fact]
    public async Task LoadAsync_WithHttpClient_403Response_FallsBackToBrowser()
    {
        // Arrange
        var httpClient = CreateMockHttpClient(HttpStatusCode.Forbidden, "Forbidden");
        var sut = CreateSut(httpClient);
        var request = new PageLoadRequest { Url = "https://example.com" };

        // The browser session will be called as a fallback.
        _browserSession.GetOrCreatePageAsync()
            .Returns<IPage>(_ => throw new PlaywrightException("No browser available"));

        // Act
        var result = await sut.LoadAsync(request);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Browser error");
    }

    [Fact]
    public async Task LoadAsync_PreferBrowser_HttpFallbackReturns3xx_SurfacesRedirectLoopVerdict()
    {
        // workspace-odn5: end-to-end coverage of the HTTP-path redirect-loop seam.
        // The podcast ReadingListContentProvider loads with PreferBrowser=true; when
        // the browser leg fails and the HTTP fallback hits a redirect loop, the
        // HttpClientHandler exhausts its auto-redirect budget and returns a FINAL 3xx.
        // That status must surface end-to-end as a typed RedirectLoop verdict
        // (TryHttpFetchAsync -> HumanActionDetector.Detect), not a bare "HTTP 302"
        // string. FakeHttpMessageHandler returns the status verbatim, standing in for
        // the post-budget 3xx response.
        var httpClient = CreateMockHttpClient(HttpStatusCode.Redirect, string.Empty); // 302
        var sut = CreateSut(httpClient);

        // Force the browser leg to fail so PreferBrowser falls back to HTTP.
        _browserSession.GetOrCreatePageAsync()
            .Returns<IPage>(_ => throw new PlaywrightException("No browser available"));

        var request = new PageLoadRequest { Url = "https://macleans.ca/", PreferBrowser = true };

        var result = await sut.LoadAsync(request);

        result.Success.Should().BeFalse();
        result.RequiredAction.Should().NotBeNull(
            "a final 3xx after the redirect budget is exhausted is a redirect loop, not an opaque HTTP error");
        result.RequiredAction!.Variant.Should().Be(HumanActionVariant.RedirectLoop);
        result.RequiredAction.Domain.Should().Be("macleans.ca");
    }

    [Fact]
    public async Task LoadAsync_WithHttpClient_CloudflareChallenge_FallsBackToBrowser()
    {
        // Arrange - Cloudflare challenge HTML should trigger JS-required detection
        var html = "<html><head><title>Just a moment...</title></head><body>Checking your browser before accessing the site. Please enable cookies.</body></html>";
        var httpClient = CreateMockHttpClient(HttpStatusCode.OK, html);
        var sut = CreateSut(httpClient);
        var request = new PageLoadRequest { Url = "https://example.com" };

        _browserSession.GetOrCreatePageAsync()
            .Returns<IPage>(_ => throw new PlaywrightException("No browser available"));

        // Act
        var result = await sut.LoadAsync(request);

        // Assert - HTTP succeeded but JS was required, so it fell back to browser (which also failed)
        result.Success.Should().BeFalse();
    }

    [Fact]
    public async Task LoadAsync_WithHttpClient_ExplicitJsRequired_FallsBackToBrowser()
    {
        // Arrange - Page with explicit "please enable javascript" message
        var html = "<html><body><noscript>Please enable JavaScript to view this page.</noscript></body></html>";
        var httpClient = CreateMockHttpClient(HttpStatusCode.OK, html);
        var sut = CreateSut(httpClient);
        var request = new PageLoadRequest { Url = "https://example.com" };

        _browserSession.GetOrCreatePageAsync()
            .Returns<IPage>(_ => throw new PlaywrightException("No browser available"));

        // Act
        var result = await sut.LoadAsync(request);

        // Assert - Should have detected JS required and fallen back to browser
        result.Success.Should().BeFalse();
    }

    [Fact]
    public async Task LoadAsync_WithHttpClient_NormalHtmlWithReactMentioned_DoesNotFallBack()
    {
        // Arrange - A normal page that mentions "react" in article text
        var html = @"<html><head><title>Article About React</title></head>
            <body><h1>How React Changed Frontend Development</h1>
            <p>React is a JavaScript library for building user interfaces.</p></body></html>";
        var httpClient = CreateMockHttpClient(HttpStatusCode.OK, html);
        var sut = CreateSut(httpClient);
        var request = new PageLoadRequest { Url = "https://example.com/article" };

        // Act
        var result = await sut.LoadAsync(request);

        // Assert - Normal page with "react" mentioned should succeed via HTTP
        result.Success.Should().BeTrue();
        result.Html.Should().Contain("React");
    }

    [Fact]
    public async Task LoadAsync_WithHttpClient_CloudflareBrowserVerification_DetectsJsRequired()
    {
        // Arrange
        var html = @"<html><head><title>Attention Required! | Cloudflare</title></head>
            <body><div id='cf-browser-verification'>Checking your browser...</div></body></html>";
        var httpClient = CreateMockHttpClient(HttpStatusCode.OK, html);
        var sut = CreateSut(httpClient);
        var request = new PageLoadRequest { Url = "https://example.com" };

        _browserSession.GetOrCreatePageAsync()
            .Returns<IPage>(_ => throw new PlaywrightException("No browser"));

        // Act
        var result = await sut.LoadAsync(request);

        // Assert
        result.Success.Should().BeFalse();
    }

    [Fact]
    public async Task LoadAsync_WithoutHttpClient_GoesDirectlyToBrowser()
    {
        // Arrange - No HttpClient injected
        var sut = CreateSut(httpClient: null);
        var request = new PageLoadRequest { Url = "https://example.com" };

        _browserSession.GetOrCreatePageAsync()
            .Returns<IPage>(_ => throw new PlaywrightException("No browser available"));

        // Act
        var result = await sut.LoadAsync(request);

        // Assert - Should have gone directly to browser (and failed since mock throws)
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Browser error");
    }

    [Fact]
    public async Task LoadAsync_CancellationRequested_ReturnsCancelledResult()
    {
        // Arrange
        var cts = new CancellationTokenSource();
        cts.Cancel();

        var httpClient = CreateMockHttpClient(HttpStatusCode.OK, "<html></html>");
        var sut = CreateSut(httpClient);
        var request = new PageLoadRequest { Url = "https://example.com" };

        _browserSession.GetOrCreatePageAsync()
            .Returns<IPage>(_ => throw new PlaywrightException("Browser unavailable"));

        // Act
        var result = await sut.LoadAsync(request, cts.Token);

        // Assert
        result.Success.Should().BeFalse();
    }

    [Fact]
    public async Task LoadAsync_WithHttpClient_ExtractsMetadata()
    {
        // Arrange
        var html = @"<html><head>
            <title>Page Title</title>
            <meta property='og:title' content='OG Title' />
            <meta name='description' content='Page description' />
            <meta name='author' content='John Doe' />
            <link rel='canonical' href='https://example.com/canonical' />
            </head><body><p>Content</p></body></html>";
        var httpClient = CreateMockHttpClient(HttpStatusCode.OK, html);
        var sut = CreateSut(httpClient);
        var request = new PageLoadRequest { Url = "https://example.com" };

        // Act
        var result = await sut.LoadAsync(request);

        // Assert
        result.Success.Should().BeTrue();
        result.Metadata.Should().NotBeNull();
        result.Metadata!.Title.Should().Be("OG Title");
        result.Metadata.Description.Should().Be("Page description");
        result.Metadata.Author.Should().Be("John Doe");
        result.Metadata.CanonicalUrl.Should().Be("https://example.com/canonical");
    }

    [Fact]
    public async Task GetPageSourceAsync_SuccessfulLoad_ReturnsHtml()
    {
        // Arrange
        var html = "<html><head><title>Test</title></head><body>Content</body></html>";
        var httpClient = CreateMockHttpClient(HttpStatusCode.OK, html);
        var sut = CreateSut(httpClient);

        // Act
        var result = await sut.GetPageSourceAsync("https://example.com");

        // Assert
        result.Should().Be(html);
    }

    [Fact]
    public async Task GetPageSourceAsync_FailedLoad_ThrowsInvalidOperationException()
    {
        // Arrange
        var sut = CreateSut(httpClient: null);

        _browserSession.GetOrCreatePageAsync()
            .Returns<IPage>(_ => throw new PlaywrightException("No browser"));

        // Act & Assert
        await FluentActions.Invoking(() => sut.GetPageSourceAsync("https://example.com"))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Failed to load page:*");
    }

    [Fact]
    public async Task LoadAsync_WithHttpClient_YouHaveBeenBlocked_DetectsJsRequired()
    {
        // Arrange
        var html = "<html><body><h1>You have been blocked</h1><p>This website is using a security service.</p></body></html>";
        var httpClient = CreateMockHttpClient(HttpStatusCode.OK, html);
        var sut = CreateSut(httpClient);
        var request = new PageLoadRequest { Url = "https://example.com" };

        _browserSession.GetOrCreatePageAsync()
            .Returns<IPage>(_ => throw new PlaywrightException("No browser"));

        // Act
        var result = await sut.LoadAsync(request);

        // Assert
        result.Success.Should().BeFalse();
    }

    [Fact]
    public async Task LoadAsync_WithHttpClient_ChallengePlatform_DetectsJsRequired()
    {
        // Arrange
        var html = "<html><body><div class='challenge-platform'>Please complete the challenge</div></body></html>";
        var httpClient = CreateMockHttpClient(HttpStatusCode.OK, html);
        var sut = CreateSut(httpClient);
        var request = new PageLoadRequest { Url = "https://example.com" };

        _browserSession.GetOrCreatePageAsync()
            .Returns<IPage>(_ => throw new PlaywrightException("No browser"));

        // Act
        var result = await sut.LoadAsync(request);

        // Assert
        result.Success.Should().BeFalse();
    }

    [Fact]
    public async Task LoadAsync_WithHttpClient_JavascriptIsRequired_DetectsJsRequired()
    {
        // Arrange
        var html = "<html><body><noscript>JavaScript is required to use this application.</noscript></body></html>";
        var httpClient = CreateMockHttpClient(HttpStatusCode.OK, html);
        var sut = CreateSut(httpClient);
        var request = new PageLoadRequest { Url = "https://example.com" };

        _browserSession.GetOrCreatePageAsync()
            .Returns<IPage>(_ => throw new PlaywrightException("No browser"));

        // Act
        var result = await sut.LoadAsync(request);

        // Assert
        result.Success.Should().BeFalse();
    }

    [Fact]
    public async Task LoadAsync_WithHttpClient_ArticleAuthorUrl_ReturnsNullAuthor()
    {
        // Arrange - only article:author with a URL, no meta[name=author]
        var html = @"<html><head>
            <title>Test Article</title>
            <meta property='article:author' content='https://www.nytimes.com/by/blacki-migliozzi' />
            </head><body><p>Content</p></body></html>";
        var httpClient = CreateMockHttpClient(HttpStatusCode.OK, html);
        var sut = CreateSut(httpClient);
        var request = new PageLoadRequest { Url = "https://example.com" };

        // Act
        var result = await sut.LoadAsync(request);

        // Assert - PageLoader should NOT expose the URL as an author name
        result.Success.Should().BeTrue();
        result.Metadata!.Author.Should().BeNull();
    }

    [Fact]
    public async Task LoadAsync_WithHttpClient_MetaNameAuthorAndArticleAuthorUrl_PrefersName()
    {
        // Arrange - both meta[name=author] (real name) and article:author (URL)
        var html = @"<html><head>
            <title>Test Article</title>
            <meta name='author' content='John Doe' />
            <meta property='article:author' content='https://www.nytimes.com/by/someone' />
            </head><body><p>Content</p></body></html>";
        var httpClient = CreateMockHttpClient(HttpStatusCode.OK, html);
        var sut = CreateSut(httpClient);
        var request = new PageLoadRequest { Url = "https://example.com" };

        // Act
        var result = await sut.LoadAsync(request);

        // Assert
        result.Success.Should().BeTrue();
        result.Metadata!.Author.Should().Be("John Doe");
    }

    [Fact]
    public async Task LoadAsync_WithHttpClient_DecodesHtmlEntitiesInTitle()
    {
        // Arrange - title contains HTML entities (e.g. smart quotes, apostrophes)
        var html = @"<html><head>
            <meta property='og:title' content='Today&#x27;s Paper' />
            </head><body><p>Content</p></body></html>";
        var httpClient = CreateMockHttpClient(HttpStatusCode.OK, html);
        var sut = CreateSut(httpClient);
        var request = new PageLoadRequest { Url = "https://example.com" };

        // Act
        var result = await sut.LoadAsync(request);

        // Assert
        result.Success.Should().BeTrue();
        result.Metadata!.Title.Should().Be("Today's Paper");
    }

    [Fact]
    public async Task LoadAsync_WithHttpClient_DecodesHtmlEntitiesInTitleTag()
    {
        // Arrange - <title> tag contains HTML entities
        var html = @"<html><head>
            <title>Today&#x27;s Paper &amp; More</title>
            </head><body><p>Content</p></body></html>";
        var httpClient = CreateMockHttpClient(HttpStatusCode.OK, html);
        var sut = CreateSut(httpClient);
        var request = new PageLoadRequest { Url = "https://example.com" };

        // Act
        var result = await sut.LoadAsync(request);

        // Assert
        result.Success.Should().BeTrue();
        result.Metadata!.Title.Should().Be("Today's Paper & More");
    }

    [Fact]
    public async Task LoadAsync_WithHttpClient_DecodesHtmlEntitiesInDescription()
    {
        // Arrange - description contains HTML entities
        var html = @"<html><head>
            <title>Test</title>
            <meta name='description' content='It&#x27;s a great day &amp; more' />
            </head><body><p>Content</p></body></html>";
        var httpClient = CreateMockHttpClient(HttpStatusCode.OK, html);
        var sut = CreateSut(httpClient);
        var request = new PageLoadRequest { Url = "https://example.com" };

        // Act
        var result = await sut.LoadAsync(request);

        // Assert
        result.Success.Should().BeTrue();
        result.Metadata!.Description.Should().Be("It's a great day & more");
    }

    [Fact]
    public async Task BrowserFetch_BotChallengeDetected_ReturnsFailureImmediately()
    {
        // Speed fix (workspace-0b9s QA #5): the bot-challenge detection must
        // surface a Failure with a typed RequiredAction immediately on the FIRST
        // ContentAsync read, not after a 60-second polling window. Previously the
        // reader paid the full BotChallengeMaxWaitMs window before showing any
        // feedback. Now the orchestrator catches the verdict at <1s and renders
        // the variant-aware "Site is showing a CAPTCHA / press R" reader-view box.
        var config = new BrowserConfiguration { BotChallengeMaxWaitMs = 60000 };
        var sut = CreateSut(httpClient: null, config: config);
        var request = new PageLoadRequest { Url = "https://example.com" };

        var challengeHtml = "<html><head></head><body><script src=\"https://captcha-delivery.com/c\"></script></body></html>";

        var page = Substitute.For<IPage>();
        var response = Substitute.For<IResponse>();
        page.GotoAsync(Arg.Any<string>(), Arg.Any<PageGotoOptions>()).Returns(Task.FromResult<IResponse?>(response));
        page.Url.Returns("https://example.com");

        // Counts how many times ContentAsync was called — proves we did NOT poll.
        var callCount = 0;
        page.ContentAsync().Returns(_ =>
        {
            callCount++;
            return Task.FromResult(challengeHtml);
        });

        page.WaitForLoadStateAsync(Arg.Any<LoadState>(), Arg.Any<PageWaitForLoadStateOptions>())
            .Returns(Task.CompletedTask);
        page.WaitForFunctionAsync(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<PageWaitForFunctionOptions>())
            .Returns(Task.FromResult(Substitute.For<IJSHandle>()));
        page.EvaluateAsync<int?>(Arg.Any<string>()).Returns(Task.FromResult<int?>(0));

        _browserSession.GetOrCreatePageAsync().Returns(Task.FromResult(page));

        // Wall-clock guard: if we're still polling, this would take ~60s. Cap at 5s.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var task = sut.LoadAsync(request);
        var completed = await Task.WhenAny(task, Task.Delay(5000));
        sw.Stop();
        completed.Should().BeSameAs(task,
            $"LoadAsync must not block on the polling window — elapsed {sw.ElapsedMilliseconds}ms (max 5000ms)");

        var result = await task;
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Bot challenge");
        result.RequiredAction.Should().NotBeNull(
            "the failure must carry a typed HumanActionRequired so the orchestrator can render the variant box");
        result.RequiredAction!.Variant.Should().Be(HumanActionVariant.Captcha);

        // ContentAsync called once for the initial check — not the multi-call polling pattern.
        callCount.Should().Be(1, "no polling: only the initial ContentAsync read");
    }

    [Fact]
    public async Task BrowserFetch_BotChallengeNeverResolves_ReturnsFailure()
    {
        // Same as above but with the original test name kept for compatibility —
        // verifies the failure path returns a typed verdict.
        var config = new BrowserConfiguration { BotChallengeMaxWaitMs = 100 };
        var sut = CreateSut(httpClient: null, config: config);
        var request = new PageLoadRequest { Url = "https://example.com" };

        var challengeHtml = "<html><head></head><body><script src=\"https://captcha-delivery.com/c\"></script></body></html>";

        var page = Substitute.For<IPage>();
        var response = Substitute.For<IResponse>();
        page.GotoAsync(Arg.Any<string>(), Arg.Any<PageGotoOptions>()).Returns(Task.FromResult<IResponse?>(response));
        page.Url.Returns("https://example.com");
        page.ContentAsync().Returns(Task.FromResult(challengeHtml));
        page.WaitForLoadStateAsync(Arg.Any<LoadState>(), Arg.Any<PageWaitForLoadStateOptions>())
            .Returns(Task.CompletedTask);
        page.WaitForFunctionAsync(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<PageWaitForFunctionOptions>())
            .Returns(Task.FromResult(Substitute.For<IJSHandle>()));
        page.EvaluateAsync<int?>(Arg.Any<string>()).Returns(Task.FromResult<int?>(0));

        _browserSession.GetOrCreatePageAsync().Returns(Task.FromResult(page));

        // Act
        var result = await sut.LoadAsync(request);

        // Assert - Should return failure with a typed action
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Bot challenge");
        result.RequiredAction.Should().NotBeNull();
    }

    [Fact]
    public async Task BrowserFetch_NoBotChallenge_ReturnsImmediately()
    {
        // Arrange
        var sut = CreateSut(httpClient: null);
        var request = new PageLoadRequest { Url = "https://example.com" };

        var normalHtml = "<html><head><title>Normal Page</title></head><body><p>Content</p></body></html>";

        var page = Substitute.For<IPage>();
        var response = Substitute.For<IResponse>();
        page.GotoAsync(Arg.Any<string>(), Arg.Any<PageGotoOptions>()).Returns(Task.FromResult<IResponse?>(response));
        page.Url.Returns("https://example.com");
        page.ContentAsync().Returns(Task.FromResult(normalHtml));
        page.WaitForLoadStateAsync(Arg.Any<LoadState>(), Arg.Any<PageWaitForLoadStateOptions>())
            .Returns(Task.CompletedTask);
        page.WaitForFunctionAsync(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<PageWaitForFunctionOptions>())
            .Returns(Task.FromResult(Substitute.For<IJSHandle>()));
        page.EvaluateAsync<int?>(Arg.Any<string>()).Returns(Task.FromResult<int?>(0));

        _browserSession.GetOrCreatePageAsync().Returns(Task.FromResult(page));

        // Act
        var result = await sut.LoadAsync(request);

        // Assert - Should return immediately without polling
        result.Success.Should().BeTrue();
        result.Html.Should().Be(normalHtml);
        result.Metadata!.Title.Should().Be("Normal Page");
    }

    [Fact]
    public async Task LoadAsync_PreferBrowser_TriesBrowserFirst_ReturnsSuccess()
    {
        // Arrange - PreferBrowser should try browser first
        var html = "<html><head><title>Browser Page</title></head><body><p>Content</p></body></html>";
        var httpClient = CreateMockHttpClient(HttpStatusCode.OK, "<html><head><title>HTTP Page</title></head><body>HTTP</body></html>");
        var sut = CreateSut(httpClient);
        var request = new PageLoadRequest { Url = "https://example.com", PreferBrowser = true };

        var page = Substitute.For<IPage>();
        var response = Substitute.For<IResponse>();
        page.GotoAsync(Arg.Any<string>(), Arg.Any<PageGotoOptions>()).Returns(Task.FromResult<IResponse?>(response));
        page.Url.Returns("https://example.com");
        page.ContentAsync().Returns(Task.FromResult(html));
        page.WaitForLoadStateAsync(Arg.Any<LoadState>(), Arg.Any<PageWaitForLoadStateOptions>())
            .Returns(Task.CompletedTask);
        page.WaitForFunctionAsync(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<PageWaitForFunctionOptions>())
            .Returns(Task.FromResult(Substitute.For<IJSHandle>()));
        page.EvaluateAsync<int?>(Arg.Any<string>()).Returns(Task.FromResult<int?>(0));

        _browserSession.GetOrCreatePageAsync().Returns(Task.FromResult(page));

        // Act
        var result = await sut.LoadAsync(request);

        // Assert - Should have used browser and returned its content
        result.Success.Should().BeTrue();
        result.Html.Should().Be(html);
        result.Metadata!.Title.Should().Be("Browser Page");
        result.FetchMethod.Should().Be(Domain.Enums.Browser.FetchMethod.Browser);
    }

    [Fact]
    public async Task LoadAsync_PreferBrowser_BrowserFails_FallsBackToHttp()
    {
        // Arrange - Browser fails, should fall back to HTTP
        var httpHtml = "<html><head><title>HTTP Fallback</title></head><body><p>Fallback content</p></body></html>";
        var httpClient = CreateMockHttpClient(HttpStatusCode.OK, httpHtml);
        var sut = CreateSut(httpClient);
        var request = new PageLoadRequest { Url = "https://example.com", PreferBrowser = true };

        _browserSession.GetOrCreatePageAsync()
            .Returns<IPage>(_ => throw new PlaywrightException("No browser available"));

        // Act
        var result = await sut.LoadAsync(request);

        // Assert - Should have fallen back to HTTP after browser failure
        result.Success.Should().BeTrue();
        result.Html.Should().Be(httpHtml);
        result.Metadata!.Title.Should().Be("HTTP Fallback");
    }

    [Fact]
    public async Task LoadAsync_PreferBrowser_BothFail_ReturnsFailure()
    {
        // Arrange - Both browser and HTTP fail
        var httpClient = CreateMockHttpClient(HttpStatusCode.InternalServerError, "Server Error");
        var sut = CreateSut(httpClient);
        var request = new PageLoadRequest { Url = "https://example.com", PreferBrowser = true };

        _browserSession.GetOrCreatePageAsync()
            .Returns<IPage>(_ => throw new PlaywrightException("No browser available"));

        // Act
        var result = await sut.LoadAsync(request);

        // Assert
        result.Success.Should().BeFalse();
    }

    [Fact]
    public async Task LoadAsync_PreferBrowser_NoHttpClient_BrowserFails_ReturnsFailure()
    {
        // Arrange - No HttpClient and browser fails
        var sut = CreateSut(httpClient: null);
        var request = new PageLoadRequest { Url = "https://example.com", PreferBrowser = true };

        _browserSession.GetOrCreatePageAsync()
            .Returns<IPage>(_ => throw new PlaywrightException("No browser available"));

        // Act
        var result = await sut.LoadAsync(request);

        // Assert - No HTTP fallback available, should return browser failure
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Browser error");
    }

    [Fact]
    public async Task LoadAsync_PreferBrowserFalse_DefaultBehavior_TriesHttpFirst()
    {
        // Arrange - PreferBrowser=false (default) should try HTTP first
        var httpHtml = "<html><head><title>HTTP First</title></head><body><p>Content</p></body></html>";
        var httpClient = CreateMockHttpClient(HttpStatusCode.OK, httpHtml);
        var sut = CreateSut(httpClient);
        var request = new PageLoadRequest { Url = "https://example.com", PreferBrowser = false };

        // Act
        var result = await sut.LoadAsync(request);

        // Assert - Should succeed via HTTP without ever touching browser
        result.Success.Should().BeTrue();
        result.Metadata!.Title.Should().Be("HTTP First");
        await _browserSession.DidNotReceive().GetOrCreatePageAsync();
    }

    [Fact]
    public async Task LoadAsync_ForceBrowserTrumpsPreferBrowser()
    {
        // Arrange - When both ForceBrowser and PreferBrowser are true,
        // ForceBrowser takes precedence (browser-only, no HTTP fallback path)
        var sut = CreateSut(httpClient: CreateMockHttpClient(HttpStatusCode.OK, "<html><head><title>HTTP</title></head><body>HTTP</body></html>"));
        var request = new PageLoadRequest { Url = "https://example.com", ForceBrowser = true, PreferBrowser = true };

        var html = "<html><head><title>Browser Page</title></head><body><p>Browser content</p></body></html>";
        var page = Substitute.For<IPage>();
        var response = Substitute.For<IResponse>();
        page.GotoAsync(Arg.Any<string>(), Arg.Any<PageGotoOptions>()).Returns(Task.FromResult<IResponse?>(response));
        page.Url.Returns("https://example.com");
        page.ContentAsync().Returns(Task.FromResult(html));
        page.WaitForLoadStateAsync(Arg.Any<LoadState>(), Arg.Any<PageWaitForLoadStateOptions>())
            .Returns(Task.CompletedTask);
        page.WaitForFunctionAsync(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<PageWaitForFunctionOptions>())
            .Returns(Task.FromResult(Substitute.For<IJSHandle>()));
        page.EvaluateAsync<int?>(Arg.Any<string>()).Returns(Task.FromResult<int?>(0));

        _browserSession.GetOrCreatePageAsync().Returns(Task.FromResult(page));

        // Act
        var result = await sut.LoadAsync(request);

        // Assert - ForceBrowser path should be used (browser only, no HTTP at all)
        result.Success.Should().BeTrue();
        result.FetchMethod.Should().Be(Domain.Enums.Browser.FetchMethod.Browser);
    }

    [Fact]
    public void PageLoadRequest_PreferBrowser_DefaultsFalse()
    {
        // Arrange & Act
        var request = new PageLoadRequest { Url = "https://example.com" };

        // Assert
        request.PreferBrowser.Should().BeFalse();
    }

    [Fact]
    public async Task DismissOverlaysAsync_ExecutesJavaScript_DoesNotThrow()
    {
        // Arrange
        var page = Substitute.For<IPage>();
        page.EvaluateAsync<int?>(Arg.Any<string>()).Returns(Task.FromResult<int?>(2));

        // Act & Assert - should not throw
        await PageLoader.DismissOverlaysAsync(page, _logger);
        await page.Received(1).EvaluateAsync<int?>(Arg.Any<string>());
    }

    [Fact]
    public async Task DismissOverlaysAsync_JavaScriptThrows_DoesNotPropagate()
    {
        // Arrange - JS execution fails (e.g., page navigating, session lost)
        var page = Substitute.For<IPage>();
        page.EvaluateAsync<int?>(Arg.Any<string>())
            .Returns<int?>(_ => throw new PlaywrightException("Session expired"));

        // Act & Assert - should swallow the exception
        var act = async () => await PageLoader.DismissOverlaysAsync(page, _logger);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task DismissOverlaysAsync_ReturnsZero_WhenNoOverlaysPresent()
    {
        // Arrange - page has no overlays, JS returns 0
        var page = Substitute.For<IPage>();
        page.EvaluateAsync<int?>(Arg.Any<string>()).Returns(Task.FromResult<int?>(0));

        // Act - should complete without logging "Dismissed N overlay"
        await PageLoader.DismissOverlaysAsync(page, _logger);
        await page.Received(1).EvaluateAsync<int?>(Arg.Any<string>());
    }

    /// <summary>
    /// Fake HttpMessageHandler for testing HttpClient-based code without real HTTP calls.
    /// </summary>
    private class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string _content;

        public FakeHttpMessageHandler(HttpStatusCode statusCode, string content)
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

/// <summary>
/// Unit tests for the stale-page heuristic (<see cref="PageLoader.LooksLikeStalePlaywrightPage"/>)
/// that drives the workspace-m7nc retry-once behaviour. Picks out the Playwright
/// "Target page, context or browser has been closed" pattern so the loader can
/// retry with a fresh page object after an out-of-band navigation (captcha solve)
/// invalidates the in-flight reference.
/// </summary>
[Trait("Category", "Unit")]
public class PageLoaderStalePageHeuristicTests
{
    [Theory]
    [InlineData("Target page, context or browser has been closed", true)]
    [InlineData("Target page, context or browser has been closed.", true)]
    [InlineData("Target closed", true)]
    [InlineData("Page.gotoAsync: Target page, context or browser has been closed", true)]
    [InlineData("context or browser has been closed", true)]
    [InlineData("Timeout 30000ms exceeded", false)]
    [InlineData("net::ERR_NAME_NOT_RESOLVED", false)]
    [InlineData("", false)]
    public void LooksLikeStalePlaywrightPage_RecognisesTargetClosedFamily(
        string errorMessage,
        bool expected)
    {
        WireCopy.Infrastructure.Browser.PageLoader.LooksLikeStalePlaywrightPage(errorMessage)
            .Should().Be(expected);
    }
}

/// <summary>
/// Integration-shape unit tests for the workspace-m7nc retry behaviour:
/// when the first <c>GotoAsync</c> throws a stale-page PlaywrightException, the
/// loader must invalidate the cached page and retry with a fresh page reference
/// instead of surfacing the raw "Target page" error to the user.
/// </summary>
[Trait("Category", "Unit")]
public class PageLoaderRetryOnStalePageTests
{
    private readonly ILogger<PageLoader> _logger;
    private readonly IOptions<BrowserConfiguration> _browserConfig;
    private readonly IBrowserSession _browserSession;

    public PageLoaderRetryOnStalePageTests()
    {
        _logger = Substitute.For<ILogger<PageLoader>>();
        _browserConfig = Options.Create(new BrowserConfiguration { BotChallengeMaxWaitMs = 100 });
        _browserSession = Substitute.For<IBrowserSession>();
        _browserSession.IsBrowserAvailable.Returns(true);
    }

    private static IPage CreateThrowingPage(string errorMessage)
    {
        var page = Substitute.For<IPage>();
        page.Url.Returns("https://example.com");
        page.GotoAsync(Arg.Any<string>(), Arg.Any<PageGotoOptions>())
            .Returns<IResponse?>(_ => throw new PlaywrightException(errorMessage));
        page.WaitForLoadStateAsync(Arg.Any<LoadState>(), Arg.Any<PageWaitForLoadStateOptions>())
            .Returns(Task.CompletedTask);
        page.WaitForFunctionAsync(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<PageWaitForFunctionOptions>())
            .Returns(Task.FromResult(Substitute.For<IJSHandle>()));
        page.EvaluateAsync<int?>(Arg.Any<string>()).Returns(Task.FromResult<int?>(0));
        return page;
    }

    private static IPage CreateHealthyPage(string html, string url)
    {
        var page = Substitute.For<IPage>();
        var response = Substitute.For<IResponse>();
        page.GotoAsync(Arg.Any<string>(), Arg.Any<PageGotoOptions>())
            .Returns(Task.FromResult<IResponse?>(response));
        page.Url.Returns(url);
        page.ContentAsync().Returns(Task.FromResult(html));
        page.WaitForLoadStateAsync(Arg.Any<LoadState>(), Arg.Any<PageWaitForLoadStateOptions>())
            .Returns(Task.CompletedTask);
        page.WaitForFunctionAsync(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<PageWaitForFunctionOptions>())
            .Returns(Task.FromResult(Substitute.For<IJSHandle>()));
        page.EvaluateAsync<int?>(Arg.Any<string>()).Returns(Task.FromResult<int?>(0));
        return page;
    }

    [Fact]
    public async Task BrowserFetch_FirstCallThrowsStalePage_RetriesAndSucceeds()
    {
        // workspace-m7nc: the named acceptance test. Mock the first GotoAsync to throw
        // a "Target page" PlaywrightException (the post-captcha stale-page shape) and
        // the second to return real HTML. LoadAsync must invalidate the cached page,
        // get a fresh IPage from the session, and succeed on the retry — instead of
        // surfacing the raw Playwright error to the user.
        var stalePage = CreateThrowingPage("Target page, context or browser has been closed");
        var freshHtml = "<html><head><title>Recovered</title></head><body><p>article body</p></body></html>";
        var freshPage = CreateHealthyPage(freshHtml, "https://example.com/article");

        _browserSession.GetOrCreatePageAsync()
            .Returns(Task.FromResult(stalePage), Task.FromResult(freshPage));

        // Force the browser path (no HttpClient → BrowserFetchAsync is taken directly).
        var sut = new PageLoader(_browserConfig, _logger, _browserSession, httpClient: null);
        var request = new PageLoadRequest { Url = "https://example.com/article", ForceBrowser = true };

        var result = await sut.LoadAsync(request);

        result.Success.Should().BeTrue(
            "the loader must retry with a fresh page when the first call hits a stale-target Playwright exception");
        result.Html.Should().Be(freshHtml);
        await _browserSession.Received(1).InvalidatePageAsync();
        await _browserSession.Received(2).GetOrCreatePageAsync();
    }

    [Fact]
    public async Task BrowserFetch_RetryAlsoThrowsStalePage_SurfacesFriendlierCopy()
    {
        // workspace-m7nc: when even the retry sees a stale target, surface the
        // user-actionable copy ("Page navigated mid-load … Press Shift+R to retry")
        // instead of the raw Playwright message.
        var stale1 = CreateThrowingPage("Target page, context or browser has been closed");
        var stale2 = CreateThrowingPage("Target closed");

        _browserSession.GetOrCreatePageAsync()
            .Returns(Task.FromResult(stale1), Task.FromResult(stale2));

        var sut = new PageLoader(_browserConfig, _logger, _browserSession, httpClient: null);
        var request = new PageLoadRequest { Url = "https://example.com/article", ForceBrowser = true };

        var result = await sut.LoadAsync(request);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Page navigated mid-load",
            "stale-page failures must surface the user-actionable copy, not the raw Playwright message");
        result.ErrorMessage.Should().Contain("Shift+R");
        await _browserSession.Received(1).InvalidatePageAsync();
        await _browserSession.Received(2).GetOrCreatePageAsync();
    }

    [Fact]
    public async Task BrowserFetch_NonStalePlaywrightFailure_DoesNotRetry()
    {
        // Non-stale Playwright errors (e.g. DNS resolution) should NOT trigger the
        // retry path — that would waste a second browser round-trip on a failure
        // that has no chance of succeeding.
        var pageWithDnsError = CreateThrowingPage("net::ERR_NAME_NOT_RESOLVED at https://invalid.example");

        _browserSession.GetOrCreatePageAsync()
            .Returns(Task.FromResult(pageWithDnsError));

        var sut = new PageLoader(_browserConfig, _logger, _browserSession, httpClient: null);
        var request = new PageLoadRequest { Url = "https://invalid.example", ForceBrowser = true };

        var result = await sut.LoadAsync(request);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("net::ERR_NAME_NOT_RESOLVED");
        await _browserSession.DidNotReceive().InvalidatePageAsync();
        await _browserSession.Received(1).GetOrCreatePageAsync();
    }

    [Fact]
    public async Task BrowserFetch_RedirectLoop_SurfacesTypedRedirectLoopVerdict()
    {
        // workspace-odn5: a Chromium redirect cycle (net::ERR_TOO_MANY_REDIRECTS,
        // confirmed live as the Playwright wrapping of a 302-looping origin and
        // intermittently on Cloudflare-fronted macleans.ca) must surface a typed
        // RedirectLoop verdict — so the orchestrator renders the actionable
        // "open in your browser, then press R" box — rather than the raw
        // "Browser error: net::ERR_TOO_MANY_REDIRECTS" string. It must NOT trigger
        // the workspace-m7nc stale-page retry (a loop is not a stale target).
        var loopingPage = CreateThrowingPage("net::ERR_TOO_MANY_REDIRECTS at https://macleans.ca/");

        _browserSession.GetOrCreatePageAsync()
            .Returns(Task.FromResult(loopingPage));

        var sut = new PageLoader(_browserConfig, _logger, _browserSession, httpClient: null);
        var request = new PageLoadRequest { Url = "https://macleans.ca/", ForceBrowser = true };

        var result = await sut.LoadAsync(request);

        result.Success.Should().BeFalse();
        result.RequiredAction.Should().NotBeNull(
            "a redirect loop is an actionable human-in-the-loop condition, not an opaque error");
        result.RequiredAction!.Variant.Should().Be(HumanActionVariant.RedirectLoop);
        result.RequiredAction.Domain.Should().Be("macleans.ca");
        result.ErrorMessage.Should().NotContain("ERR_TOO_MANY_REDIRECTS",
            "the raw Chromium net-error string must not leak into the user-facing failure message");

        // A loop is not a stale target — no wasted invalidate-and-retry round-trip.
        await _browserSession.DidNotReceive().InvalidatePageAsync();
        await _browserSession.Received(1).GetOrCreatePageAsync();
    }

    [Fact]
    public async Task BrowserFetch_NavigationInterruptedIntoErrorPage_SurfacesGenericVerdict()
    {
        // workspace-odn5: a sibling of the redirect cycle observed live on
        // macleans.ca — the Cloudflare consent/bot bounce supersedes the real
        // navigation and lands on chrome-error://chromewebdata/. Surface the
        // deliberately non-committal Generic verdict (root cause is ambiguous)
        // so the user still gets an actionable box instead of the raw multi-line
        // Playwright "interrupted by another navigation" string.
        var bouncedPage = CreateThrowingPage(
            "Navigation to \"https://macleans.ca/\" is interrupted by another navigation to \"chrome-error://chromewebdata/\"");

        _browserSession.GetOrCreatePageAsync()
            .Returns(Task.FromResult(bouncedPage));

        var sut = new PageLoader(_browserConfig, _logger, _browserSession, httpClient: null);
        var request = new PageLoadRequest { Url = "https://macleans.ca/", ForceBrowser = true };

        var result = await sut.LoadAsync(request);

        result.Success.Should().BeFalse();
        result.RequiredAction.Should().NotBeNull();
        result.RequiredAction!.Variant.Should().Be(HumanActionVariant.Generic);
        result.RequiredAction.Domain.Should().Be("macleans.ca");
        result.ErrorMessage.Should().NotContain("chrome-error",
            "the raw Playwright navigation-interruption string must not leak into the user-facing message");
        await _browserSession.DidNotReceive().InvalidatePageAsync();
    }
}

/// <summary>
/// Unit tests for the redirect-loop heuristic
/// (<see cref="PageLoader.LooksLikeRedirectLoop"/>) that drives the workspace-odn5
/// typed-verdict path. Recognises the Chromium <c>net::ERR_TOO_MANY_REDIRECTS</c>
/// net error (and the humanised "too many redirects" phrasing) so the loader can
/// surface a <see cref="HumanActionVariant.RedirectLoop"/> box instead of a raw
/// browser-error string.
/// </summary>
[Trait("Category", "Unit")]
public class PageLoaderRedirectLoopHeuristicTests
{
    [Theory]
    [InlineData("net::ERR_TOO_MANY_REDIRECTS at https://macleans.ca/", true)]
    [InlineData("Page.goto: net::ERR_TOO_MANY_REDIRECTS at https://www.macleans.ca/", true)]
    [InlineData("err_too_many_redirects", true)] // case-insensitive
    [InlineData("The site reported too many redirects", true)] // humanised phrasing
    [InlineData("net::ERR_NAME_NOT_RESOLVED at https://invalid.example", false)]
    [InlineData("Target page, context or browser has been closed", false)]
    [InlineData("Timeout 30000ms exceeded", false)]
    [InlineData("", false)]
    public void LooksLikeRedirectLoop_RecognisesTooManyRedirects(string errorMessage, bool expected)
    {
        PageLoader.LooksLikeRedirectLoop(errorMessage).Should().Be(expected);
    }

    [Theory]
    // Failed bounce — BOTH markers present (the macleans.ca shape captured live).
    [InlineData("Navigation to \"https://macleans.ca/\" is interrupted by another navigation to \"chrome-error://chromewebdata/\"", true)]
    [InlineData("INTERRUPTED BY ANOTHER NAVIGATION to CHROME-ERROR://x", true)] // case-insensitive
    // Healthy fast redirect — interrupted, but to a real URL (no chrome-error): must NOT fire.
    [InlineData("Navigation to \"https://a.com/\" is interrupted by another navigation to \"https://a.com/home\"", false)]
    // chrome-error alone, without the interruption phrasing: must NOT fire.
    [InlineData("net::ERR_FAILED at chrome-error://chromewebdata/", false)]
    [InlineData("net::ERR_TOO_MANY_REDIRECTS at https://macleans.ca/", false)]
    [InlineData("", false)]
    public void LooksLikeInterruptedNavigation_RequiresBothMarkers(string errorMessage, bool expected)
    {
        PageLoader.LooksLikeInterruptedNavigation(errorMessage).Should().Be(expected);
    }
}
