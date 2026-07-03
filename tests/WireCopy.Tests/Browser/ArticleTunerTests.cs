// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using HtmlAgilityPack;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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

/// <summary>
/// workspace-8qyo: the article tuner's candidate discovery and — critically —
/// the truncation guard: an ignored region must be REMOVED, never allowed to
/// end the article early.
/// </summary>
public class ArticleTunerTests
{
    private const string ArticleHtml = """
        <!DOCTYPE html><html><head><title>T</title></head><body>
          <header><h1 class="headline">Big News Headline</h1></header>
          <article class="story-body">
            <p>First paragraph of the story with plenty of words to count here. The quick brown fox jumps over the lazy dog while reporters keep typing detailed sentences about the unfolding situation on the ground today.</p>
            <p>Second paragraph keeps the narrative going with more detail. The quick brown fox jumps over the lazy dog while reporters keep typing detailed sentences about the unfolding situation on the ground today.</p>
            <div class="related-promo"><h3>Related coverage</h3><a href="/x">Other story</a></div>
            <p>Third paragraph AFTER the promo continues the article body text. The quick brown fox jumps over the lazy dog while reporters keep typing detailed sentences about the unfolding situation on the ground today.</p>
            <p>Fourth paragraph closes out the story with a final thought. The quick brown fox jumps over the lazy dog while reporters keep typing detailed sentences about the unfolding situation on the ground today.</p>
          </article>
          <aside class="newsletter">Sign up for our newsletter</aside>
        </body></html>
        """;

    [Fact]
    public void ExcludedRegion_NeverTruncatesTheArticle()
    {
        var config = new ArticleSelectorConfig
        {
            Domain = "example.com",
            PageTypes =
            [
                new PageTypeEntry
                {
                    Name = "tuned",
                    Priority = 80,
                    Selectors = new ArticleSelectors
                    {
                        Headline = ["//h1[contains(@class,'headline')]"],
                        Body = ["//article[contains(@class,'story-body')]"],
                        ExcludeRegions = ["//*[contains(@class,'related-promo')]"],
                    },
                },
            ],
        };

        var extractor = new SelectorBasedArticleExtractor(NullLogger<SelectorBasedArticleExtractor>.Instance);
        var content = extractor.Extract(config, "https://example.com/story", ArticleHtml);

        content.Should().NotBeNull();
        content!.Title.Should().Be("Big News Headline");
        var text = string.Join("\n", content.Paragraphs);
        text.Should().Contain("Third paragraph AFTER the promo",
            "the ignored promo must be removed, NOT treated as the end of the article");
        text.Should().Contain("Fourth paragraph closes out");
        text.Should().NotContain("Related coverage", "the ignored region is gone");
    }

    [Fact]
    public void BuildCandidates_ReturnsOnlyMatchingProbes_AiSeedFirst()
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(ArticleHtml);

        var candidates = ArticleTunerHandler.BuildCandidates(
            doc,
            aiSeed: ["//h1[contains(@class,'headline')]"],
            probes: ["//h1", "//h2", "//*[@itemprop='headline']"],
            minMatches: 1,
            maxMatches: 5);

        candidates.Should().NotBeEmpty();
        candidates[0].XPath.Should().Be("//h1[contains(@class,'headline')]", "the AI seed leads");
        candidates.Select(c => c.XPath).Should().Contain("//h1");
        candidates.Select(c => c.XPath).Should().NotContain("//h2", "nothing matches it");
    }

    [Fact]
    public void BuildBodyCandidates_RanksByParagraphDensity()
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(ArticleHtml);

        var candidates = ArticleTunerHandler.BuildBodyCandidates(doc, aiSeed: null);

        candidates.Should().NotBeEmpty();
        candidates[0].XPath.Should().Be("//article",
            "the article element holds the densest paragraph cluster");
    }

    // ---- workspace-3uzl.7: AI seed origin is carried on each candidate ----

    [Fact]
    public void BuildCandidates_FlagsAiSeedOrigin()
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(ArticleHtml);

        var candidates = ArticleTunerHandler.BuildCandidates(
            doc,
            aiSeed: ["//h1[contains(@class,'headline')]"],
            probes: ["//h1"],
            minMatches: 1,
            maxMatches: 5);

        candidates.Should().HaveCountGreaterThanOrEqualTo(2);
        candidates[0].IsAiSeed.Should().BeTrue("the seed-derived candidate is marked");
        candidates.Single(c => c.XPath == "//h1").IsAiSeed.Should().BeFalse("probe candidates are not");
    }

    [Fact]
    public void BuildBodyCandidates_FlagsAiSeedOrigin_AndKeepsSeedFirst()
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(ArticleHtml);

        var candidates = ArticleTunerHandler.BuildBodyCandidates(
            doc, aiSeed: ["//article[contains(@class,'story-body')]"]);

        candidates[0].XPath.Should().Be("//article[contains(@class,'story-body')]");
        candidates[0].IsAiSeed.Should().BeTrue();
        candidates.Skip(1).Should().OnlyContain(c => !c.IsAiSeed);
    }

    [Fact]
    public void BuildCard_MarksAiSeedCandidates_InTheLabel()
    {
        var card = ArticleTunerHandler.BuildCard(
            "Tune layout — 1/3 Headline",
            "Which element is the headline?",
            [("//h1[@class='hl']", 1, true), ("//h1", 2, false)],
            cursor: 0,
            selected: null,
            hint: "j/k or ↑/↓: next candidate · Enter: confirm · Esc: cancel",
            lensAvailable: true);

        card.Options[0].Label.Should().EndWith(" (AI)", "seed-derived entries show their origin");
        card.Options[1].Label.Should().NotContain("(AI)");
    }

    // ---- workspace-3uzl.2: the sidecar promise is honest ----

    [Fact]
    public void BuildCard_Footnote_ReflectsLensAvailability()
    {
        List<(string XPath, int Matches, bool IsAiSeed)> candidates = [("//h1", 1, false)];

        ArticleTunerHandler.BuildCard("t", "p", candidates, 0, null, "h", lensAvailable: true)
            .Footnote.Should().Be("highlighted live in the sidecar");
        ArticleTunerHandler.BuildCard("t", "p", candidates, 0, null, "h", lensAvailable: false)
            .Footnote.Should().Be("sidecar not docked — showing match counts only",
                "without a lens page the card must not promise live highlighting");
    }

    // ---- workspace-3uzl.4: back navigation + self-test retry keep the tuner alive ----

    [Fact]
    public async Task HandleTune_BackKey_ReturnsToPreviousStep_PreservingTheFlow()
    {
        // Headline candidates for ArticleHtml, in order: //h1, //header//h1,
        // //*[contains(@class,'headline')]. Body candidates: //article first.
        var fixture = new TunerFixture(
            Cmd(CommandType.ActivateLink),          // step 1: confirm //h1
            Key('h'),                               // step 2: back to step 1
            Cmd(CommandType.MoveDown),              // step 1: cursor → //header//h1
            Cmd(CommandType.ActivateLink),          // step 1: confirm //header//h1
            Cmd(CommandType.ActivateLink),          // step 2: confirm //article
            Cmd(CommandType.ActivateLink));         // step 3: done (no ignores)
        fixture.SelfTestReturns(SomeContent());

        await ArticleTunerHandler.HandleTuneAsync(fixture.Ctx, fixture.Options, CancellationToken.None);

        await fixture.Store.Received(1).SaveAsync(Arg.Is<ArticleSelectorConfig>(c =>
            c.PageTypes[0].Selectors.Headline.SequenceEqual(new[] { "//header//h1" }) &&
            c.PageTypes[0].Selectors.Body.SequenceEqual(new[] { "//article" })));
    }

    [Fact]
    public async Task HandleTune_SelfTestFailure_ReturnsToIgnoreStep_InsteadOfTearingDown()
    {
        var fixture = new TunerFixture(
            Cmd(CommandType.ActivateLink),          // step 1: confirm //h1
            Cmd(CommandType.ActivateLink),          // step 2: confirm //article
            Cmd(CommandType.ActivateLink),          // step 3: done → self-test FAILS
            Cmd(CommandType.ToggleSelection),       // step 3 again (overlay alive): mark //aside
            Cmd(CommandType.ActivateLink));         // step 3: done → self-test passes
        fixture.SelfTestReturns(null, SomeContent());

        await ArticleTunerHandler.HandleTuneAsync(fixture.Ctx, fixture.Options, CancellationToken.None);

        fixture.SelectorExtractor.Received(2).Extract(
            Arg.Any<ArticleSelectorConfig>(), Arg.Any<string>(), Arg.Any<string>());
        await fixture.Store.Received(1).SaveAsync(Arg.Is<ArticleSelectorConfig>(c =>
            c.PageTypes[0].Selectors.ExcludeRegions.Contains("//aside")));
    }

    [Fact]
    public async Task HandleTune_Escape_CancelsWithoutSaving()
    {
        var fixture = new TunerFixture(
            Cmd(CommandType.ActivateLink),          // step 1: confirm
            Cmd(CommandType.GoBack));               // step 2: Esc cancels

        await ArticleTunerHandler.HandleTuneAsync(fixture.Ctx, fixture.Options, CancellationToken.None);

        await fixture.Store.DidNotReceive().SaveAsync(Arg.Any<ArticleSelectorConfig>());
        fixture.NavigationService.CurrentContext.StatusMessage.Should().Contain("cancelled");
    }

    private static NavigationCommand Cmd(CommandType type) => new() { Type = type };

    private static NavigationCommand Key(char c) => new() { Type = CommandType.NoOp, RawKeyChar = c };

    private static ReadableContent SomeContent() => ReadableContent.Create(
        "Big News Headline",
        "First paragraph of the story.",
        ["First paragraph of the story."]);

    /// <summary>Scripted CommandContext for driving HandleTuneAsync end to end (no AI, no lens).</summary>
    private sealed class TunerFixture
    {
        public TunerFixture(params NavigationCommand[] script)
        {
            NavigationService = new NavigationService(Substitute.For<ILogger<NavigationService>>());
            var page = Page.Create(
                "https://example.com/2026/06/28/story",
                ArticleHtml,
                new PageMetadata { Title = "Story" });
            NavigationService.NavigateTo(page);
            NavigationService.SetViewMode(ViewMode.Readable);

            SelectorExtractor = Substitute.For<ISelectorBasedArticleExtractor>();
            Store = Substitute.For<IArticleLayoutStore>();

            var services = new ServiceCollection();
            services.AddSingleton(SelectorExtractor);
            services.AddSingleton(Store);
            var provider = services.BuildServiceProvider();

            var input = Substitute.For<IInputHandler>();
            input.WaitForInputAsync(Arg.Any<CancellationToken>())
                .Returns(script[0], script.Skip(1).ToArray());

            Options = new RenderOptions { TerminalWidth = 80, TerminalHeight = 24, MaxContentWidth = 80 };
            var themeProvider = Substitute.For<IThemeProvider>();
            themeProvider.CurrentTheme.Returns(ThemeName.Phosphor);

            Ctx = new CommandContext
            {
                NavigationService = NavigationService,
                Renderer = Substitute.For<IPageRenderer>(),
                InputHandler = input,
                ScopeFactory = provider.GetRequiredService<IServiceScopeFactory>(),
                Logger = Substitute.For<ILogger>(),
                PageCache = Substitute.For<IPageCache>(),
                LineCacheManager = new LineCacheManager(NavigationService, themeProvider),
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
                GetCurrentRenderOptions = () => Options!,
                CreateCollectionService = _ => Substitute.For<WireCopy.Application.Interfaces.ICollectionService>(),
                GetReaderViewportHeight = _ => 20,
                GetHierarchicalViewportHeight = _ => 20,
                AdjustScrollForSelection = (_, _) => { },
                ScrollToSearchMatch = (_, _) => { },
            };
        }

        public NavigationService NavigationService { get; }

        public ISelectorBasedArticleExtractor SelectorExtractor { get; }

        public IArticleLayoutStore Store { get; }

        public CommandContext Ctx { get; }

        public RenderOptions Options { get; }

        public void SelfTestReturns(params ReadableContent?[] results)
        {
            SelectorExtractor.Extract(Arg.Any<ArticleSelectorConfig>(), Arg.Any<string>(), Arg.Any<string>())
                .Returns(results[0], results.Skip(1).ToArray());
        }
    }
}
