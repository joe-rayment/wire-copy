// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces;
using WireCopy.Infrastructure.Browser;
using WireCopy.Infrastructure.BrowserExtension;
using WireCopy.Infrastructure.Configuration;
using WireCopy.Infrastructure.Podcast.Cache;
using WireCopy.Persistence;
using WireCopy.Persistence.Repositories;
using Xunit;

namespace WireCopy.Tests.BrowserExtension;

/// <summary>
/// End-to-end tests of the extension's "save this page" path (workspace-m7aml): real link,
/// readability and saved-layout extractors, a real article cache on disk and a real
/// (in-memory SQLite) Reading List — only the browser itself is absent.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ReadingListCaptureTests : TestDatabaseFixture
{
    private const string ArticleUrl = "https://news.example.com/2026/10/11/some-story";
    private const string ArticleHeadline = "Council approves flood barrier along the waterfront";
    private const string StoryMarker = "flood barrier along the eastern waterfront";
    private const string DecoyMarker = "sold-out jazz festival";

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "wirecopy-capture-tests-" + Guid.NewGuid().ToString("N"));
    private readonly ArticleLayoutStore _layoutStore;
    private readonly ArticleContentCache _cache;
    private readonly ServiceProvider _services;
    private readonly ReadingListCapture _sut;

    public ReadingListCaptureTests()
    {
        _layoutStore = new ArticleLayoutStore(NullLogger<ArticleLayoutStore>.Instance, Path.Combine(_tempDir, "layouts"));
        _cache = new ArticleContentCache(
            Path.Combine(_tempDir, "cache"), TimeSpan.FromHours(1), 100, NullLogger<ArticleContentCache>.Instance);

        // ICollectionService is scoped: build the same chain the app registers
        // (DbContext -> unit of work -> repository -> service) over the fixture's SQLite database.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => CreateDbContext());
        services.AddSingleton<ICollectionPreferences, InMemoryCollectionPreferences>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<ICollectionRepository, CollectionRepository>();
        services.AddScoped<ICollectionService, WireCopy.Infrastructure.Collections.CollectionService>();
        _services = services.BuildServiceProvider();

        _sut = new ReadingListCapture(
            new LinkExtractor(NullLogger<LinkExtractor>.Instance),
            new ReadableContentExtractor(NullLogger<ReadableContentExtractor>.Instance),
            _layoutStore,
            new SelectorBasedArticleExtractor(NullLogger<SelectorBasedArticleExtractor>.Instance),
            _cache,
            Options.Create(new BrowserConfiguration()),
            _services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ReadingListCapture>.Instance);
    }

    // Guards:   A saved per-site layout wins over generic readability: the page's real story text is
    //           cached (not the bigger decoy block next to it) and the Reading List row gets the headline.
    // Fails if: SaveAsync ignores the saved layout, or caches/labels the decoy article instead of the story.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter SaveAsync_SavedSiteLayout_CachesStoryTextNotDecoy
    [Fact]
    public async Task SaveAsync_SavedSiteLayout_CachesStoryTextNotDecoy()
    {
        await SaveStoryTextLayoutAsync();

        var result = await _sut.SaveAsync(ArticleUrl, "Tab title | News Example", BuildArticlePage(), CancellationToken.None);

        result.Source.Should().Be(ArticleHtmlExtraction.SourceSiteLayout);
        result.Source.Should().Be("site-layout");
        result.Title.Should().Be(ArticleHeadline);
        result.Words.Should().BeGreaterThan(100);
        result.AlreadySaved.Should().BeFalse();

        var cached = await _cache.TryGetAsync(ArticleUrl);
        cached.Should().NotBeNull();
        cached!.CleanedText.Should().Contain(StoryMarker);
        cached.CleanedText.Should().NotContain(DecoyMarker);

        var items = await ReadingListItemsAsync();
        items.Should().ContainSingle();
        items[0].Url.Should().Be(ArticleUrl);
        items[0].Title.Should().Be(ArticleHeadline);
    }

    // Guards:   With no saved layout for the site, the same page is parsed by generic readability
    //           and the article is still cached and saved to the Reading List.
    // Fails if: the readability fallback is skipped, or the source label is wrong.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter SaveAsync_NoSavedLayout_UsesReadability
    [Fact]
    public async Task SaveAsync_NoSavedLayout_UsesReadability()
    {
        var result = await _sut.SaveAsync(ArticleUrl, "Tab title | News Example", BuildArticlePage(), CancellationToken.None);

        result.Source.Should().Be(ArticleHtmlExtraction.SourceReadability);
        result.Source.Should().Be("readability");
        result.Words.Should().BeGreaterThan(100);

        var cached = await _cache.TryGetAsync(ArticleUrl);
        cached.Should().NotBeNull();
        cached!.CleanedText.Should().NotBeNullOrWhiteSpace();

        var items = await ReadingListItemsAsync();
        items.Should().ContainSingle();
        items[0].Title.Should().Be(ArticleHeadline);
    }

    // Guards:   Saving the same page twice keeps one Reading List row and tells the caller it was already there.
    // Fails if: the second save adds a duplicate row, or AlreadySaved is not reported.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter SaveAsync_SameUrlTwice_ReportsAlreadySavedAndKeepsOneRow
    [Fact]
    public async Task SaveAsync_SameUrlTwice_ReportsAlreadySavedAndKeepsOneRow()
    {
        var html = BuildArticlePage();

        var first = await _sut.SaveAsync(ArticleUrl, "Tab title", html, CancellationToken.None);
        var second = await _sut.SaveAsync(ArticleUrl, "Tab title", html, CancellationToken.None);

        first.AlreadySaved.Should().BeFalse();
        second.AlreadySaved.Should().BeTrue();

        var items = await ReadingListItemsAsync();
        items.Should().ContainSingle();
        items[0].Url.Should().Be(ArticleUrl);
        (await _sut.ReadingListCountAsync(CancellationToken.None)).Should().Be(1);
    }

    // Guards:   A section/link-list page has no article body: nothing is extracted, but the page is
    //           still saved to the Reading List under the browser tab's title.
    // Fails if: a link list is mis-read as an article, or the row is dropped / saved without the tab title.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter SaveAsync_LinkListPage_SavesRowWithPageTitleAndNoArticle
    [Fact]
    public async Task SaveAsync_LinkListPage_SavesRowWithPageTitleAndNoArticle()
    {
        const string url = "https://news.example.com/world";
        const string tabTitle = "World news - latest headlines | News Example";

        var result = await _sut.SaveAsync(url, tabTitle, BuildLinkListPage(), CancellationToken.None);

        result.Source.Should().Be(ArticleHtmlExtraction.SourceNone);
        result.Source.Should().Be("none");
        result.Words.Should().Be(0);
        result.Title.Should().Be(tabTitle);
        (await _cache.TryGetAsync(url)).Should().BeNull();

        var items = await ReadingListItemsAsync();
        items.Should().ContainSingle();
        items[0].Url.Should().Be(url);
        items[0].Title.Should().Be(tabTitle);
    }

    // Guards:   Only web pages can be saved: non-http(s) addresses are refused and nothing is written.
    // Fails if: SaveAsync accepts ftp:, file:, or text that is not a URL, or saves a row before refusing.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter SaveAsync_NonHttpUrl_Throws
    [Theory]
    [InlineData("ftp://news.example.com/file.txt")]
    [InlineData("file:///etc/passwd")]
    [InlineData("not a url")]
    public async Task SaveAsync_NonHttpUrl_Throws(string url)
    {
        var act = () => _sut.SaveAsync(url, "Title", BuildArticlePage(), CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
        (await ReadingListItemsAsync()).Should().BeEmpty();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _services.Dispose();
            try
            {
                Directory.Delete(_tempDir, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort temp cleanup.
            }
        }

        base.Dispose(disposing);
    }

    private async Task SaveStoryTextLayoutAsync()
    {
        var domain = ArticleLayoutDomains.FromUrl(ArticleUrl)!;
        await _layoutStore.SaveAsync(new ArticleSelectorConfig
        {
            Domain = domain,
            PageTypes =
            [
                new PageTypeEntry
                {
                    Name = "article",
                    Selectors = new ArticleSelectors
                    {
                        // The extractor runs these through HtmlAgilityPack's SelectNodes, so they are XPath, not CSS.
                        Headline = ["//h1[contains(@class, 'headline')]"],
                        Body = ["//div[contains(@class, 'story-text')]"],
                    },
                },
            ],
        });
    }

    private async Task<IReadOnlyList<WireCopy.Domain.Entities.Collections.CollectionItem>> ReadingListItemsAsync()
    {
        using var scope = _services.CreateScope();
        var collections = scope.ServiceProvider.GetRequiredService<ICollectionService>();
        var readingList = await collections.GetReadingListAsync();
        return readingList.Items.ToList();
    }

    private static string BuildArticlePage()
    {
        string[] story =
        [
            "The city council voted on Tuesday night to approve a new flood barrier along the eastern waterfront, ending three years of debate over who should pay for the project.",
            "Construction will begin next spring and is expected to take eighteen months, according to the engineers who presented the final plan to a packed chamber at city hall.",
            "Residents of the low-lying harbour district, who have watched water creep into their basements after every major storm, welcomed the decision with a long round of applause.",
            "Opponents argued that the money would be better spent on drainage upgrades inland, though several of them conceded that the waterfront had been neglected for decades.",
            "Funding will come from a mix of state grants, a modest increase in the stormwater fee and a federal loan that the mayor said she expects to be confirmed within weeks.",
            "Officials promised monthly public updates once work starts, including a map of closed streets and a telephone line for businesses affected by the construction.",
        ];

        string[] decoy =
        [
            "Tickets for the sold-out jazz festival went on sale at dawn and vanished within minutes, leaving thousands of fans queuing online for a chance at the few returns.",
            "Organisers say the three-day event will fill every hotel room in the old town, and local restaurants have already begun hiring extra kitchen staff for the weekend.",
            "Headliners include a veteran saxophonist making a rare appearance after a long recovery, along with two young trios that have built loyal followings through small clubs.",
            "Neighbours have raised concerns about noise after midnight, so the festival committee agreed to move the final stage indoors on Saturday and Sunday evenings.",
            "Volunteers are still needed for stewarding, first aid and recycling duty, and anyone interested can sign up at the community centre before the end of the month.",
            "Public transport will run extra late services across the festival weekend, with additional trams from the main station until two in the morning for ticket holders.",
            "Street food vendors from across the region will set up along the river path, offering everything from wood-fired flatbreads to vegan dumplings and fresh lemonade.",
            "Weather forecasters expect a warm and dry weekend, which should please organisers who lost an entire evening to heavy rain at last year's open-air concert.",
        ];

        var storyHtml = string.Concat(story.Select(p => $"<p>{p}</p>"));
        var decoyHtml = string.Concat(decoy.Select(p => $"<p>{p}</p>"));

        return $$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <title>{{ArticleHeadline}} | News Example</title>
              <meta property="og:type" content="article">
              <meta property="og:title" content="{{ArticleHeadline}}">
              <script type="application/ld+json">{"@context":"https://schema.org","@type":"NewsArticle","headline":"{{ArticleHeadline}}","datePublished":"2026-10-11T08:00:00Z"}</script>
            </head>
            <body>
              <main>
                <h1 class="headline">{{ArticleHeadline}}</h1>
                <div class="story-text">{{storyHtml}}</div>
                <article class="more-coverage">
                  <h2>More from the region</h2>
                  {{decoyHtml}}
                </article>
              </main>
            </body>
            </html>
            """;
    }

    private static string BuildLinkListPage()
    {
        var links = string.Concat(Enumerable.Range(1, 36).Select(i =>
            $"<li><a href=\"https://news.example.com/2026/10/11/headline-story-number-{i}\">Headline story number {i} from around the world today</a></li>"));

        return $$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <title>World news - latest headlines | News Example</title>
              <meta property="og:type" content="website">
            </head>
            <body>
              <nav><a href="/">Home</a><a href="/world">World</a></nav>
              <ul class="headline-list">{{links}}</ul>
            </body>
            </html>
            """;
    }
}
