// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using TermReader.Application.DTOs.Browser;
using TermReader.Application.Interfaces;
using TermReader.Application.Interfaces.Browser;
using TermReader.Domain.Enums.Browser;
using TermReader.Infrastructure.Browser;
using TermReader.Infrastructure.Browser.CommandHandlers;
using TermReader.Infrastructure.Configuration;
using Xunit;

namespace TermReader.Tests.Browser;

/// <summary>
/// Tests for the <c>:cookies</c> command (status, import, clear).
/// </summary>
[Trait("Category", "Unit")]
public class CookiesCommandHandlerTests
{
    private readonly NavigationService _navigationService;
    private readonly CommandContext _ctx;
    private readonly RenderOptions _options;
    private readonly ICookieManager _cookieManager;
    private readonly IBrowserSession _browserSession;
    private readonly IHttpCookieRefresher _httpRefresher;
    private bool _renderCalled;

    public CookiesCommandHandlerTests()
    {
        var navLogger = Substitute.For<ILogger<NavigationService>>();
        _navigationService = new NavigationService(navLogger);

        var page = Domain.Entities.Browser.Page.Create(
            "https://example.com",
            "<html><body>Test</body></html>",
            new Domain.ValueObjects.Browser.PageMetadata { Title = "Test" });
        _navigationService.NavigateTo(page);

        _cookieManager = Substitute.For<ICookieManager>();
        _browserSession = Substitute.For<IBrowserSession>();
        _httpRefresher = Substitute.For<IHttpCookieRefresher>();

        var browserConfig = Options.Create(new BrowserConfiguration
        {
            PaywalledDomains = new[] { "nytimes.com", "wsj.com" },
        });

        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IServiceScope>();
        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(ICookieManager)).Returns(_cookieManager);
        serviceProvider.GetService(typeof(IBrowserSession)).Returns(_browserSession);
        serviceProvider.GetService(typeof(IHttpCookieRefresher)).Returns(_httpRefresher);
        serviceProvider.GetService(typeof(IOptions<BrowserConfiguration>)).Returns(browserConfig);
        scope.ServiceProvider.Returns(serviceProvider);
        scopeFactory.CreateScope().Returns(scope);

        _options = new RenderOptions
        {
            TerminalWidth = 80,
            TerminalHeight = 24,
            MaxContentWidth = 80,
        };

        var themeProvider = Substitute.For<IThemeProvider>();
        themeProvider.CurrentTheme.Returns(ThemeName.Phosphor);

        _ctx = new CommandContext
        {
            NavigationService = _navigationService,
            Renderer = Substitute.For<IPageRenderer>(),
            InputHandler = Substitute.For<IInputHandler>(),
            ScopeFactory = scopeFactory,
            Logger = Substitute.For<ILogger>(),
            PageCache = Substitute.For<IPageCache>(),
            LineCacheManager = new LineCacheManager(_navigationService, themeProvider),
            ThemeProvider = themeProvider,
            PreloadService = Substitute.For<IPreloadService>(),
            LayoutVariantProvider = Substitute.For<ILayoutVariantProvider>(),
            NavigateToAsync = (_, _, _) => Task.CompletedTask,
            ForceRefreshAsync = (_, _, _) => Task.CompletedTask,
            InteractiveRefreshAsync = (_, _, _) => Task.CompletedTask,
            RenderCurrentPageAsync = (_, _) =>
            {
                _renderCalled = true;
                return Task.CompletedTask;
            },
            RefreshCollectionsAsync = _ => Task.CompletedTask,
            RefreshBookmarksAsync = _ => Task.CompletedTask,
            GetCurrentRenderOptions = () => _options,
            CreateCollectionService = _ => Substitute.For<ICollectionService>(),
            GetReaderViewportHeight = _ => 20,
            GetHierarchicalViewportHeight = _ => 20,
            AdjustScrollForSelection = (_, _) => { },
            ScrollToSearchMatch = (_, _) => { },
        };
    }

    [Fact]
    public async Task Status_NoCookieFile_ShowsHelpfulMessage()
    {
        _cookieManager.GetCookieInfoAsync()
            .Returns(Task.FromResult<CookieInfo?>(new CookieInfo { Exists = false }));

        await SearchCommandHandler.HandleCommandLineInput(
            _ctx, "cookies", _options, CancellationToken.None);

        _navigationService.CurrentContext.StatusMessage.Should()
            .Contain("No cookies stored")
            .And.Contain(":cookies import");
        _renderCalled.Should().BeTrue();
    }

    [Fact]
    public async Task Status_CookieFileExists_ShowsPerDomainBreakdown()
    {
        _cookieManager.GetCookieInfoAsync().Returns(Task.FromResult<CookieInfo?>(new CookieInfo
        {
            Exists = true,
            CookieCount = 5,
            CreatedAt = DateTime.UtcNow.AddHours(-3),
            ExpiresAt = DateTime.UtcNow.AddDays(20),
            IsExpired = false,
            IsEncrypted = true,
            Version = 2,
        }));

        _cookieManager.LoadCookiesAsync().Returns(Task.FromResult<IReadOnlyList<StoredCookie>>(new[]
        {
            new StoredCookie("nyt-s", "abc", ".nytimes.com", "/", DateTime.UtcNow.AddDays(20)),
            new StoredCookie("nyt-a", "def", ".nytimes.com", "/", DateTime.UtcNow.AddDays(20)),
            new StoredCookie("wsj_s", "wsj1", ".wsj.com", "/", DateTime.UtcNow.AddDays(20)),
        }));

        await SearchCommandHandler.HandleCommandLineInput(
            _ctx, "cookies status", _options, CancellationToken.None);

        var msg = _navigationService.CurrentContext.StatusMessage!;
        msg.Should().Contain("nytimes.com: 2");
        msg.Should().Contain("wsj.com: 1");
        msg.Should().Contain("expires in");
    }

    [Fact]
    public async Task Import_NoBrowserContext_TellsUserToOpenSiteFirst()
    {
        _browserSession.HasBrowserContext.Returns(false);

        await SearchCommandHandler.HandleCommandLineInput(
            _ctx, "cookies import", _options, CancellationToken.None);

        _navigationService.CurrentContext.StatusMessage.Should()
            .Contain("Browser session not active");
        await _cookieManager.DidNotReceive().SaveCookiesAsync(
            Arg.Any<IReadOnlyList<StoredCookie>>(), Arg.Any<CancellationToken>());
        await _httpRefresher.DidNotReceive().RefreshAsync();
    }

    [Fact]
    public async Task Import_AggregatesCookiesAcrossDomains_PersistsAndRefreshes()
    {
        _browserSession.HasBrowserContext.Returns(true);

        var nytCookies = new List<StoredCookie>
        {
            new("nyt-s", "value1", ".nytimes.com", "/", DateTime.UtcNow.AddDays(30)),
            new("session-token", "token1", ".nytimes.com", "/", DateTime.UtcNow.AddDays(30)),
        };
        var wsjCookies = new List<StoredCookie>
        {
            new("wsj_session", "wsjtoken", ".wsj.com", "/", DateTime.UtcNow.AddDays(30)),
        };

        _browserSession.GetCookiesForUrlAsync("https://nytimes.com/")
            .Returns(Task.FromResult<IReadOnlyList<StoredCookie>>(nytCookies));
        _browserSession.GetCookiesForUrlAsync("https://wsj.com/")
            .Returns(Task.FromResult<IReadOnlyList<StoredCookie>>(wsjCookies));

        IReadOnlyList<StoredCookie>? saved = null;
        _cookieManager
            .When(m => m.SaveCookiesAsync(Arg.Any<IReadOnlyList<StoredCookie>>(), Arg.Any<CancellationToken>()))
            .Do(call => saved = call.Arg<IReadOnlyList<StoredCookie>>());
        _cookieManager.SaveCookiesAsync(Arg.Any<IReadOnlyList<StoredCookie>>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _httpRefresher.RefreshAsync().Returns(Task.CompletedTask);

        await SearchCommandHandler.HandleCommandLineInput(
            _ctx, "cookies import", _options, CancellationToken.None);

        await _cookieManager.Received(1).SaveCookiesAsync(
            Arg.Any<IReadOnlyList<StoredCookie>>(), Arg.Any<CancellationToken>());
        await _httpRefresher.Received(1).RefreshAsync();

        saved.Should().NotBeNull();
        saved!.Count.Should().Be(3, "all three cookies aggregated across nytimes + wsj");
        saved.Select(c => c.Name).Should().Contain(new[] { "nyt-s", "session-token", "wsj_session" });

        var msg = _navigationService.CurrentContext.StatusMessage!;
        msg.Should().Contain("Imported 3 cookies");
        msg.Should().Contain("2 domain(s)");
        msg.Should().Contain("nytimes.com");
        msg.Should().Contain("wsj.com");
    }

    [Fact]
    public async Task Import_NoCookiesInBrowser_TellsUserToLogIn()
    {
        _browserSession.HasBrowserContext.Returns(true);
        _browserSession.GetCookiesForUrlAsync(Arg.Any<string>())
            .Returns(Task.FromResult<IReadOnlyList<StoredCookie>>(Array.Empty<StoredCookie>()));

        await SearchCommandHandler.HandleCommandLineInput(
            _ctx, "cookies import", _options, CancellationToken.None);

        _navigationService.CurrentContext.StatusMessage.Should()
            .Contain("No cookies found in browser session");
        await _cookieManager.DidNotReceive().SaveCookiesAsync(
            Arg.Any<IReadOnlyList<StoredCookie>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Clear_FileExists_DeletesAndShowsMessage()
    {
        _cookieManager.ClearCookiesAsync().Returns(Task.FromResult(true));
        _httpRefresher.RefreshAsync().Returns(Task.CompletedTask);

        await SearchCommandHandler.HandleCommandLineInput(
            _ctx, "cookies clear", _options, CancellationToken.None);

        await _cookieManager.Received(1).ClearCookiesAsync();
        _navigationService.CurrentContext.StatusMessage.Should()
            .Contain("Cookies cleared");
    }

    [Fact]
    public async Task Clear_NoFile_ShowsHelpfulMessage()
    {
        _cookieManager.ClearCookiesAsync().Returns(Task.FromResult(false));

        await SearchCommandHandler.HandleCommandLineInput(
            _ctx, "cookies clear", _options, CancellationToken.None);

        _navigationService.CurrentContext.StatusMessage.Should()
            .Contain("No cookies file");
    }

    [Fact]
    public async Task UnknownSubcommand_ShowsErrorMessage()
    {
        await SearchCommandHandler.HandleCommandLineInput(
            _ctx, "cookies wibble", _options, CancellationToken.None);

        _navigationService.CurrentContext.StatusMessage.Should()
            .Contain("Unknown :cookies subcommand");
    }
}
