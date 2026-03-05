// Educational and personal use only.

using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using TermReader.Application.DTOs.Browser;
using TermReader.Infrastructure.Browser;
using TermReader.Infrastructure.Configuration;
using Xunit;

namespace TermReader.Tests.Browser;

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
    }

    private PageLoader CreateSut(HttpClient? httpClient = null)
    {
        return new PageLoader(_browserConfig, _logger, _browserSession, httpClient);
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
        result.Url.Should().StartWith("https://example.com");
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
        // Since we haven't set up the mock driver to return valid data,
        // we expect a failure from the browser path too.
        _browserSession.GetOrCreateDriver(Arg.Any<bool>())
            .Returns(_ => throw new OpenQA.Selenium.WebDriverException("No browser available"));

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

        _browserSession.GetOrCreateDriver(Arg.Any<bool>())
            .Returns(_ => throw new OpenQA.Selenium.WebDriverException("No browser available"));

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

        _browserSession.GetOrCreateDriver(Arg.Any<bool>())
            .Returns(_ => throw new OpenQA.Selenium.WebDriverException("No browser available"));

        // Act
        var result = await sut.LoadAsync(request);

        // Assert - Should have detected JS required and fallen back to browser
        result.Success.Should().BeFalse();
    }

    [Fact]
    public async Task LoadAsync_WithHttpClient_NormalHtmlWithReactMentioned_DoesNotFallBack()
    {
        // Arrange - A normal page that mentions "react" in article text
        // The simplified heuristic should NOT treat this as JS-required
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

        _browserSession.GetOrCreateDriver(Arg.Any<bool>())
            .Returns(_ => throw new OpenQA.Selenium.WebDriverException("No browser"));

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

        _browserSession.GetOrCreateDriver(Arg.Any<bool>())
            .Returns(_ => throw new OpenQA.Selenium.WebDriverException("No browser available"));

        // Act
        var result = await sut.LoadAsync(request);

        // Assert - Should have gone directly to browser (and failed since mock driver throws)
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

        // When the cancelled token causes the HTTP fetch to fail,
        // the loader falls back to browser. Mock the browser to also fail.
        _browserSession.GetOrCreateDriver(Arg.Any<bool>())
            .Returns(_ => throw new OpenQA.Selenium.WebDriverException("Browser unavailable"));

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

        _browserSession.GetOrCreateDriver(Arg.Any<bool>())
            .Returns(_ => throw new OpenQA.Selenium.WebDriverException("No browser"));

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

        _browserSession.GetOrCreateDriver(Arg.Any<bool>())
            .Returns(_ => throw new OpenQA.Selenium.WebDriverException("No browser"));

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

        _browserSession.GetOrCreateDriver(Arg.Any<bool>())
            .Returns(_ => throw new OpenQA.Selenium.WebDriverException("No browser"));

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

        _browserSession.GetOrCreateDriver(Arg.Any<bool>())
            .Returns(_ => throw new OpenQA.Selenium.WebDriverException("No browser"));

        // Act
        var result = await sut.LoadAsync(request);

        // Assert
        result.Success.Should().BeFalse();
    }

    [Fact]
    public async Task LoadAsync_WithHttpClient_ArticleAuthorUrl_ReturnsNullAuthor()
    {
        // Arrange — only article:author with a URL, no meta[name=author]
        var html = @"<html><head>
            <title>Test Article</title>
            <meta property='article:author' content='https://www.nytimes.com/by/blacki-migliozzi' />
            </head><body><p>Content</p></body></html>";
        var httpClient = CreateMockHttpClient(HttpStatusCode.OK, html);
        var sut = CreateSut(httpClient);
        var request = new PageLoadRequest { Url = "https://example.com" };

        // Act
        var result = await sut.LoadAsync(request);

        // Assert — PageLoader should NOT expose the URL as an author name
        result.Success.Should().BeTrue();
        result.Metadata!.Author.Should().BeNull();
    }

    [Fact]
    public async Task LoadAsync_WithHttpClient_MetaNameAuthorAndArticleAuthorUrl_PrefersName()
    {
        // Arrange — both meta[name=author] (real name) and article:author (URL)
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
