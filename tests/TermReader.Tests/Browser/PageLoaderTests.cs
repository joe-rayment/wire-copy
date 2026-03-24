// Educational and personal use only.

using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using NSubstitute;
using TermReader.Application.DTOs.Browser;
using TermReader.Infrastructure.Browser;
using TermReader.Infrastructure.Configuration;
using Xunit;

namespace TermReader.Tests.Browser;

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
        _browserSession.GetOrCreatePageAsync(Arg.Any<bool>())
            .Returns<IPage>(_ => throw new PlaywrightException("No browser available"));

        // Act
        var result = await sut.LoadAsync(request);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Browser error");
    }

    [Fact]
    public async Task LoadAsync_WithHttpClient_CloudflareChallenge_FallsBackToBrowser()
    {
        // Arrange - Cloudflare challenge HTML should trigger JS-required detection
        var html = "<html><head><title>Just a moment...</title></head><body>Checking your browser before accessing the site. Please enable cookies.</body></html>";
        var httpClient = CreateMockHttpClient(HttpStatusCode.OK, html);
        var sut = CreateSut(httpClient);
        var request = new PageLoadRequest { Url = "https://example.com" };

        _browserSession.GetOrCreatePageAsync(Arg.Any<bool>())
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

        _browserSession.GetOrCreatePageAsync(Arg.Any<bool>())
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

        _browserSession.GetOrCreatePageAsync(Arg.Any<bool>())
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

        _browserSession.GetOrCreatePageAsync(Arg.Any<bool>())
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

        _browserSession.GetOrCreatePageAsync(Arg.Any<bool>())
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

        _browserSession.GetOrCreatePageAsync(Arg.Any<bool>())
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

        _browserSession.GetOrCreatePageAsync(Arg.Any<bool>())
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

        _browserSession.GetOrCreatePageAsync(Arg.Any<bool>())
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

        _browserSession.GetOrCreatePageAsync(Arg.Any<bool>())
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
    public async Task BrowserFetch_BotChallengeResolves_ReturnsResolvedContent()
    {
        // Arrange - Fast polling for test speed
        var config = new BrowserConfiguration { BotChallengePollIntervalMs = 50, BotChallengeMaxWaitMs = 5000 };
        var sut = CreateSut(httpClient: null, config: config);
        var request = new PageLoadRequest { Url = "https://example.com" };

        var challengeHtml = "<html><head></head><body><script src=\"https://captcha-delivery.com/c\"></script></body></html>";
        var resolvedHtml = "<html><head><title>Real Page</title></head><body><p>Real content here</p></body></html>";

        var page = Substitute.For<IPage>();
        var response = Substitute.For<IResponse>();
        page.GotoAsync(Arg.Any<string>(), Arg.Any<PageGotoOptions>()).Returns(Task.FromResult<IResponse?>(response));
        page.Url.Returns("https://example.com");

        // First call returns challenge, subsequent calls return resolved
        var callCount = 0;
        page.ContentAsync().Returns(_ =>
        {
            callCount++;
            return Task.FromResult(callCount <= 2 ? challengeHtml : resolvedHtml);
        });

        page.WaitForLoadStateAsync(Arg.Any<LoadState>(), Arg.Any<PageWaitForLoadStateOptions>())
            .Returns(Task.CompletedTask);
        page.WaitForFunctionAsync(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<PageWaitForFunctionOptions>())
            .Returns(Task.FromResult(Substitute.For<IJSHandle>()));
        page.EvaluateAsync<int?>(Arg.Any<string>()).Returns(Task.FromResult<int?>(0));

        _browserSession.GetOrCreatePageAsync(Arg.Any<bool>()).Returns(Task.FromResult(page));

        // Act
        var result = await sut.LoadAsync(request);

        // Assert - Should have polled and returned the resolved content
        result.Success.Should().BeTrue();
        result.Html.Should().Be(resolvedHtml);
        result.Metadata!.Title.Should().Be("Real Page");
    }

    [Fact]
    public async Task BrowserFetch_BotChallengeNeverResolves_ReturnsFailure()
    {
        // Arrange - Very short max wait so the test completes quickly
        var config = new BrowserConfiguration { BotChallengePollIntervalMs = 50, BotChallengeMaxWaitMs = 100 };
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

        _browserSession.GetOrCreatePageAsync(Arg.Any<bool>()).Returns(Task.FromResult(page));

        // Act
        var result = await sut.LoadAsync(request);

        // Assert - Should return failure after polling times out
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Bot challenge");
    }

    [Fact]
    public async Task BrowserFetch_BotChallengePolling_PlaywrightException_ReturnsFailure()
    {
        // Arrange - Browser crashes during polling
        var config = new BrowserConfiguration { BotChallengePollIntervalMs = 50, BotChallengeMaxWaitMs = 5000 };
        var sut = CreateSut(httpClient: null, config: config);
        var request = new PageLoadRequest { Url = "https://example.com" };

        var challengeHtml = "<html><head></head><body><script src=\"https://captcha-delivery.com/c\"></script></body></html>";

        var page = Substitute.For<IPage>();
        var response = Substitute.For<IResponse>();
        page.GotoAsync(Arg.Any<string>(), Arg.Any<PageGotoOptions>()).Returns(Task.FromResult<IResponse?>(response));
        page.Url.Returns("https://example.com");

        // First call returns challenge, second throws (browser crash)
        var callCount = 0;
        page.ContentAsync().Returns(_ =>
        {
            callCount++;
            if (callCount <= 2)
            {
                return Task.FromResult(challengeHtml);
            }

            throw new PlaywrightException("Session lost");
        });

        page.WaitForLoadStateAsync(Arg.Any<LoadState>(), Arg.Any<PageWaitForLoadStateOptions>())
            .Returns(Task.CompletedTask);
        page.WaitForFunctionAsync(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<PageWaitForFunctionOptions>())
            .Returns(Task.FromResult(Substitute.For<IJSHandle>()));
        page.EvaluateAsync<int?>(Arg.Any<string>()).Returns(Task.FromResult<int?>(0));

        _browserSession.GetOrCreatePageAsync(Arg.Any<bool>()).Returns(Task.FromResult(page));

        // Act
        var result = await sut.LoadAsync(request);

        // Assert - Should return failure when browser crashes during polling
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Bot challenge");
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

        _browserSession.GetOrCreatePageAsync(Arg.Any<bool>()).Returns(Task.FromResult(page));

        // Act
        var result = await sut.LoadAsync(request);

        // Assert - Should return immediately without polling
        result.Success.Should().BeTrue();
        result.Html.Should().Be(normalHtml);
        result.Metadata!.Title.Should().Be("Normal Page");
    }

    [Fact]
    public async Task LoadAsync_PreferSelenium_TriesBrowserFirst_ReturnsSuccess()
    {
        // Arrange - PreferSelenium should try browser first
        var html = "<html><head><title>Selenium Page</title></head><body><p>Content</p></body></html>";
        var httpClient = CreateMockHttpClient(HttpStatusCode.OK, "<html><head><title>HTTP Page</title></head><body>HTTP</body></html>");
        var sut = CreateSut(httpClient);
        var request = new PageLoadRequest { Url = "https://example.com", PreferSelenium = true };

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

        _browserSession.GetOrCreatePageAsync(Arg.Any<bool>()).Returns(Task.FromResult(page));

        // Act
        var result = await sut.LoadAsync(request);

        // Assert - Should have used browser and returned its content
        result.Success.Should().BeTrue();
        result.Html.Should().Be(html);
        result.Metadata!.Title.Should().Be("Selenium Page");
        result.FetchMethod.Should().Be(Domain.Enums.Browser.FetchMethod.Selenium);
    }

    [Fact]
    public async Task LoadAsync_PreferSelenium_BrowserFails_FallsBackToHttp()
    {
        // Arrange - Browser fails, should fall back to HTTP
        var httpHtml = "<html><head><title>HTTP Fallback</title></head><body><p>Fallback content</p></body></html>";
        var httpClient = CreateMockHttpClient(HttpStatusCode.OK, httpHtml);
        var sut = CreateSut(httpClient);
        var request = new PageLoadRequest { Url = "https://example.com", PreferSelenium = true };

        _browserSession.GetOrCreatePageAsync(Arg.Any<bool>())
            .Returns<IPage>(_ => throw new PlaywrightException("No browser available"));

        // Act
        var result = await sut.LoadAsync(request);

        // Assert - Should have fallen back to HTTP after browser failure
        result.Success.Should().BeTrue();
        result.Html.Should().Be(httpHtml);
        result.Metadata!.Title.Should().Be("HTTP Fallback");
    }

    [Fact]
    public async Task LoadAsync_PreferSelenium_BothFail_ReturnsFailure()
    {
        // Arrange - Both browser and HTTP fail
        var httpClient = CreateMockHttpClient(HttpStatusCode.InternalServerError, "Server Error");
        var sut = CreateSut(httpClient);
        var request = new PageLoadRequest { Url = "https://example.com", PreferSelenium = true };

        _browserSession.GetOrCreatePageAsync(Arg.Any<bool>())
            .Returns<IPage>(_ => throw new PlaywrightException("No browser available"));

        // Act
        var result = await sut.LoadAsync(request);

        // Assert
        result.Success.Should().BeFalse();
    }

    [Fact]
    public async Task LoadAsync_PreferSelenium_NoHttpClient_BrowserFails_ReturnsFailure()
    {
        // Arrange - No HttpClient and browser fails
        var sut = CreateSut(httpClient: null);
        var request = new PageLoadRequest { Url = "https://example.com", PreferSelenium = true };

        _browserSession.GetOrCreatePageAsync(Arg.Any<bool>())
            .Returns<IPage>(_ => throw new PlaywrightException("No browser available"));

        // Act
        var result = await sut.LoadAsync(request);

        // Assert - No HTTP fallback available, should return browser failure
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Browser error");
    }

    [Fact]
    public async Task LoadAsync_PreferSeleniumFalse_DefaultBehavior_TriesHttpFirst()
    {
        // Arrange - PreferSelenium=false (default) should try HTTP first
        var httpHtml = "<html><head><title>HTTP First</title></head><body><p>Content</p></body></html>";
        var httpClient = CreateMockHttpClient(HttpStatusCode.OK, httpHtml);
        var sut = CreateSut(httpClient);
        var request = new PageLoadRequest { Url = "https://example.com", PreferSelenium = false };

        // Act
        var result = await sut.LoadAsync(request);

        // Assert - Should succeed via HTTP without ever touching browser
        result.Success.Should().BeTrue();
        result.Metadata!.Title.Should().Be("HTTP First");
        await _browserSession.DidNotReceive().GetOrCreatePageAsync(Arg.Any<bool>());
    }

    [Fact]
    public async Task LoadAsync_ForceBrowserTrumpsPreferSelenium()
    {
        // Arrange - When both ForceBrowser and PreferSelenium are true,
        // ForceBrowser takes precedence (browser-only, no HTTP fallback path)
        var sut = CreateSut(httpClient: CreateMockHttpClient(HttpStatusCode.OK, "<html><head><title>HTTP</title></head><body>HTTP</body></html>"));
        var request = new PageLoadRequest { Url = "https://example.com", ForceBrowser = true, PreferSelenium = true };

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

        _browserSession.GetOrCreatePageAsync(Arg.Any<bool>()).Returns(Task.FromResult(page));

        // Act
        var result = await sut.LoadAsync(request);

        // Assert - ForceBrowser path should be used (browser only, no HTTP at all)
        result.Success.Should().BeTrue();
        result.FetchMethod.Should().Be(Domain.Enums.Browser.FetchMethod.Selenium);
    }

    [Fact]
    public void PageLoadRequest_PreferSelenium_DefaultsFalse()
    {
        // Arrange & Act
        var request = new PageLoadRequest { Url = "https://example.com" };

        // Assert
        request.PreferSelenium.Should().BeFalse();
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
