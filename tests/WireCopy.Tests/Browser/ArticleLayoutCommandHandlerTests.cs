// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Entities.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Browser;
using WireCopy.Infrastructure.Browser.CommandHandlers;
using Xunit;

namespace WireCopy.Tests.Browser;

[Trait("Category", "Unit")]
public class ArticleLayoutCommandHandlerTests
{
    private readonly NavigationService _navigationService;
    private readonly IAiArticleExtractor _aiExtractor;
    private readonly ISelectorBasedArticleExtractor _selectorExtractor;
    private readonly IArticleLayoutStore _store;
    private readonly CommandContext _ctx;
    private readonly RenderOptions _options;

    public ArticleLayoutCommandHandlerTests()
    {
        _navigationService = new NavigationService(Substitute.For<ILogger<NavigationService>>());
        var page = Page.Create(
            "https://example.com/2026/05/09/story",
            "<html><body><article><p>real long body content with enough words and characters to be meaningful</p></article></body></html>",
            new PageMetadata { Title = "Story" });
        _navigationService.NavigateTo(page);
        _navigationService.SetViewMode(ViewMode.Readable);

        _aiExtractor = Substitute.For<IAiArticleExtractor>();
        _aiExtractor.IsConfigured.Returns(true);

        _selectorExtractor = Substitute.For<ISelectorBasedArticleExtractor>();
        _store = Substitute.For<IArticleLayoutStore>();

        var services = new ServiceCollection();
        services.AddSingleton(_aiExtractor);
        services.AddSingleton(_selectorExtractor);
        services.AddSingleton(_store);
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        _options = new RenderOptions { TerminalWidth = 80, TerminalHeight = 24, MaxContentWidth = 80 };
        var themeProvider = Substitute.For<IThemeProvider>();
        themeProvider.CurrentTheme.Returns(ThemeName.Phosphor);
        var lineCacheManager = new LineCacheManager(_navigationService, themeProvider);

        _ctx = new CommandContext
        {
            NavigationService = _navigationService,
            Renderer = Substitute.For<IPageRenderer>(),
            InputHandler = Substitute.For<IInputHandler>(),
            ScopeFactory = scopeFactory,
            Logger = Substitute.For<ILogger>(),
            PageCache = Substitute.For<IPageCache>(),
            LineCacheManager = lineCacheManager,
            ThemeProvider = themeProvider,
            PreloadService = Substitute.For<IPreloadService>(),
            LayoutVariantProvider = Substitute.For<ILayoutVariantProvider>(),
            NavigateToAsync = (_, _, _) => Task.CompletedTask,
            ForceRefreshAsync = (_, _, _) => Task.CompletedTask,
            InteractiveRefreshAsync = (_, _, _) => Task.CompletedTask,
            OpenInteractiveBrowserAsync = (_, _, _) => Task.CompletedTask,
            SetOverlayPainter = _ => { },
            RenderCurrentPageAsync = (_, _) => Task.CompletedTask,
            RefreshCollectionsAsync = _ => Task.CompletedTask,
            RefreshBookmarksAsync = _ => Task.CompletedTask,
            GetCurrentRenderOptions = () => _options,
            CreateCollectionService = _ => Substitute.For<Application.Interfaces.ICollectionService>(),
            GetReaderViewportHeight = _ => 20,
            GetHierarchicalViewportHeight = _ => 20,
            AdjustScrollForSelection = (_, _) => { },
            ScrollToSearchMatch = (_, _) => { },
        };
    }

    [Fact]
    public async Task HandleRegenerate_NotInReaderView_NoAiCall()
    {
        _navigationService.SetViewMode(ViewMode.Hierarchical);

        await ArticleLayoutCommandHandler.HandleRegenerateAsync(_ctx, _options, CancellationToken.None);

        await _aiExtractor.DidNotReceive().AnalyzeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        _navigationService.CurrentContext.StatusMessage.Should().Contain("reader view");
    }

    [Fact]
    public async Task HandleRegenerate_AiNotConfigured_NoAiCall()
    {
        _aiExtractor.IsConfigured.Returns(false);

        await ArticleLayoutCommandHandler.HandleRegenerateAsync(_ctx, _options, CancellationToken.None);

        await _aiExtractor.DidNotReceive().AnalyzeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        _navigationService.CurrentContext.StatusMessage.Should().Contain("OpenAI key");
    }

    [Fact]
    public async Task HandleRegenerate_AiReturnsNull_NoSave()
    {
        _aiExtractor.AnalyzeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((ArticleSelectorConfig?)null);

        await ArticleLayoutCommandHandler.HandleRegenerateAsync(_ctx, _options, CancellationToken.None);

        await _store.DidNotReceive().SaveAsync(Arg.Any<ArticleSelectorConfig>());
        _navigationService.CurrentContext.StatusMessage.Should().Contain("no layout");
    }

    [Fact]
    public async Task HandleRegenerate_SelfTestFails_NoSave()
    {
        var candidate = MakeCandidate("article");
        _aiExtractor.AnalyzeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(candidate);
        _selectorExtractor.Extract(Arg.Any<ArticleSelectorConfig>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns((ReadableContent?)null);

        await ArticleLayoutCommandHandler.HandleRegenerateAsync(_ctx, _options, CancellationToken.None);

        await _store.DidNotReceive().SaveAsync(Arg.Any<ArticleSelectorConfig>());
        _navigationService.CurrentContext.StatusMessage.Should().Contain("didn't match");
    }

    [Fact]
    public async Task HandleRegenerate_SelfTestPasses_SavesMergedConfig()
    {
        var candidate = MakeCandidate("article");
        _aiExtractor.AnalyzeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(candidate);
        _selectorExtractor.Extract(Arg.Any<ArticleSelectorConfig>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(ReadableContent.Create("Title", "p1 body text content", new List<string> { "p1 body text content" }));
        _store.LoadAsync(Arg.Any<string>()).Returns((ArticleSelectorConfig?)null);

        await ArticleLayoutCommandHandler.HandleRegenerateAsync(_ctx, _options, CancellationToken.None);

        await _store.Received(1).SaveAsync(Arg.Is<ArticleSelectorConfig>(c => c.PageTypes.Count == 1));
        _navigationService.CurrentContext.StatusMessage.Should().Contain("regenerated");
    }

    [Fact]
    public async Task HandleRegenerate_ExistingConfigSameName_ReplacesEntry()
    {
        var existing = MakeCandidate("article");
        existing.PageTypes[0].Selectors.Headline.Add("//old-headline");
        var candidate = MakeCandidate("article");
        candidate.PageTypes[0].Selectors.Headline.Add("//new-headline");

        _aiExtractor.AnalyzeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(candidate);
        _selectorExtractor.Extract(Arg.Any<ArticleSelectorConfig>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(ReadableContent.Create("Title", "p1 body text content", new List<string> { "p1 body text content" }));
        _store.LoadAsync(Arg.Any<string>()).Returns(existing);

        ArticleSelectorConfig? saved = null;
        await _store.SaveAsync(Arg.Do<ArticleSelectorConfig>(c => saved = c));

        await ArticleLayoutCommandHandler.HandleRegenerateAsync(_ctx, _options, CancellationToken.None);

        saved.Should().NotBeNull();
        saved!.PageTypes.Should().HaveCount(1, "matching name should replace, not append");
        saved.PageTypes[0].Selectors.Headline.Should().Contain("//new-headline");
        saved.PageTypes[0].Selectors.Headline.Should().NotContain("//old-headline");
    }

    [Fact]
    public async Task HandleRegenerate_ExistingConfigDifferentName_AppendsEntry()
    {
        var existing = MakeCandidate("article");
        var candidate = MakeCandidate("live-blog");

        _aiExtractor.AnalyzeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(candidate);
        _selectorExtractor.Extract(Arg.Any<ArticleSelectorConfig>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(ReadableContent.Create("Title", "p1 body text content", new List<string> { "p1 body text content" }));
        _store.LoadAsync(Arg.Any<string>()).Returns(existing);

        ArticleSelectorConfig? saved = null;
        await _store.SaveAsync(Arg.Do<ArticleSelectorConfig>(c => saved = c));

        await ArticleLayoutCommandHandler.HandleRegenerateAsync(_ctx, _options, CancellationToken.None);

        saved.Should().NotBeNull();
        saved!.PageTypes.Should().HaveCount(2, "different name should append");
        saved.PageTypes.Select(p => p.Name).Should().BeEquivalentTo(new[] { "article", "live-blog" });
    }

    private static ArticleSelectorConfig MakeCandidate(string name)
    {
        return new ArticleSelectorConfig
        {
            Domain = "example.com",
            PageTypes = new List<PageTypeEntry>
            {
                new PageTypeEntry
                {
                    Name = name,
                    PageType = PageType.Article,
                    Priority = 10,
                    Matcher = new PageTypeMatcher(),
                    Selectors = new ArticleSelectors(),
                    Quality = new ArticleQualityThresholds { MinWords = 100, MinParagraphs = 3 },
                    Provenance = new ProvenanceInfo { Model = "gpt-test", SampleUrl = "https://example.com/story" },
                },
            },
        };
    }
}
