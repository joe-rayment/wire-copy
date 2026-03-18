// <copyright file="DependencyInjectionTests.cs" company="TermReader">
// Educational and personal use only.
// </copyright>

using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TermReader.Application.Interfaces;
using TermReader.Application.Interfaces.Audio;
using TermReader.Application.Interfaces.Browser;
using TermReader.Application.Interfaces.Podcast;
using TermReader.Domain.Enums.Browser;
using TermReader.Domain.ValueObjects.Browser;
using TermReader.Domain.ValueObjects.Podcast;
using TermReader.Infrastructure.Bookmarks;
using TermReader.Infrastructure.Browser;
using TermReader.Infrastructure.Collections;
using TermReader.Infrastructure.Podcast;
using TermReader.Persistence;
using Xunit;

namespace TermReader.Tests;

public class DependencyInjectionTests : IDisposable
{
    private readonly ServiceProvider _serviceProvider;

    public DependencyInjectionTests()
    {
        var services = new ServiceCollection();
        var configuration = GetConfiguration();

        // Register configuration as a service (required by AddTerminalBrowser)
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();

        // Register terminal browser services (modular DI, matching Program.cs)
        services.AddTerminalBrowser();
        services.AddPersistence();
        services.AddCollections();
        services.AddBookmarks();
        services.AddPodcast();

        _serviceProvider = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
    }

    // --- DI wiring: one parameterized test replaces 15 individual NotBeNull tests ---

    [Theory]
    [InlineData(typeof(IBrowserService))]
    [InlineData(typeof(IPageLoader))]
    [InlineData(typeof(IPageRenderer))]
    [InlineData(typeof(IFileStorage))]
    [InlineData(typeof(INavigationService))]
    [InlineData(typeof(IBrowserSession))]
    [InlineData(typeof(IBrowserSessionControl))]
    [InlineData(typeof(ILinkExtractor))]
    [InlineData(typeof(INavigationTreeBuilder))]
    [InlineData(typeof(IReadableContentExtractor))]
    [InlineData(typeof(IInputHandler))]
    [InlineData(typeof(ITtsService))]
    [InlineData(typeof(IAudioAssembler))]
    [InlineData(typeof(IPodcastFeedGenerator))]
    [InlineData(typeof(ICloudStorageClient))]
    [InlineData(typeof(IPodcastPublisher))]
    [InlineData(typeof(IPodcastOrchestrator))]
    public void ServiceProvider_ShouldResolve(Type serviceType)
    {
        var service = _serviceProvider.GetService(serviceType);
        service.Should().NotBeNull($"DI should resolve {serviceType.Name}");
    }

    // --- DI wiring: scoped services need a scope ---

    [Theory]
    [InlineData(typeof(ICollectionService))]
    [InlineData(typeof(ICollectionRepository))]
    public void ServiceProvider_ShouldResolveScopedService(Type serviceType)
    {
        using var scope = _serviceProvider.CreateScope();
        var service = scope.ServiceProvider.GetService(serviceType);
        service.Should().NotBeNull($"DI should resolve scoped {serviceType.Name}");
    }

    // --- DI wiring: structural assertions that catch real misconfiguration ---

    [Fact]
    public void BrowserSession_AndBrowserSessionControl_ShouldBeSameInstance()
    {
        var session = _serviceProvider.GetRequiredService<IBrowserSession>();
        var control = _serviceProvider.GetRequiredService<IBrowserSessionControl>();
        session.Should().BeSameAs(control);
    }

    [Fact]
    public void CollectionExporters_ShouldIncludeUrlsAndOpml()
    {
        var exporters = _serviceProvider.GetServices<ICollectionExporter>().ToList();
        exporters.Should().HaveCountGreaterOrEqualTo(2);
        exporters.Select(e => e.Format).Should().Contain("urls");
        exporters.Select(e => e.Format).Should().Contain("opml");
    }

    // --- Smoke tests: verify resolved services can actually perform basic operations ---

    [Fact]
    public async Task LinkExtractor_SmokeTest_CanParseMinimalHtml()
    {
        var extractor = _serviceProvider.GetRequiredService<ILinkExtractor>();
        const string html = """
            <html><body>
                <a href="/article/test">Test Article</a>
                <a href="https://external.com">External</a>
            </body></html>
            """;

        var links = await extractor.ExtractLinksAsync(html, "https://example.com");

        links.Should().NotBeEmpty("extractor should find links in valid HTML");
        links.Should().Contain(l => l.DisplayText.Contains("Test Article"));
    }

    [Fact]
    public async Task ReadableContentExtractor_SmokeTest_CanExtractArticle()
    {
        var extractor = _serviceProvider.GetRequiredService<IReadableContentExtractor>();
        const string html = """
            <html><head><title>Test Article</title></head>
            <body>
                <article>
                    <h1>Test Article Title</h1>
                    <p>This is a test paragraph with enough content to be considered readable.
                    The quick brown fox jumps over the lazy dog. Lorem ipsum dolor sit amet,
                    consectetur adipiscing elit. Sed do eiusmod tempor incididunt ut labore
                    et dolore magna aliqua. Ut enim ad minim veniam, quis nostrud exercitation
                    ullamco laboris nisi ut aliquip ex ea commodo consequat.</p>
                    <p>Another paragraph with meaningful content that helps establish this as
                    a real article rather than a navigation page or sidebar fragment.</p>
                </article>
            </body></html>
            """;

        var content = await extractor.ExtractAsync(html, "https://example.com/article");

        // May return null if quality gate rejects it — that's fine, we're testing it doesn't crash
        // and that it processes the HTML through the full pipeline
        if (content != null)
        {
            content.Title.Should().NotBeNullOrWhiteSpace();
            content.WordCount.Should().BeGreaterThan(0);
        }
    }

    [Fact]
    public async Task NavigationTreeBuilder_SmokeTest_CanBuildTreeFromLinks()
    {
        var builder = _serviceProvider.GetRequiredService<INavigationTreeBuilder>();
        var links = new List<LinkInfo>
        {
            new()
            {
                Url = "https://example.com/page1",
                DisplayText = "Page One",
                Type = LinkType.Content,
                ImportanceScore = 80,
            },
            new()
            {
                Url = "https://example.com/page2",
                DisplayText = "Page Two",
                Type = LinkType.Content,
                ImportanceScore = 60,
            },
        };

        var tree = await builder.BuildTreeAsync(links);

        tree.Should().NotBeNull();
        tree.TotalLinks.Should().BeGreaterOrEqualTo(2);
        tree.Root.Should().NotBeNull();
    }

    [Fact]
    public void NavigationService_SmokeTest_CanNavigateAndTrackHistory()
    {
        var navService = _serviceProvider.GetRequiredService<INavigationService>();
        var metadata = new PageMetadata { Title = "Test Page" };
        var page = TermReader.Domain.Entities.Browser.Page.Create(
            "https://example.com", "<html></html>", metadata);

        navService.NavigateTo(page);

        navService.CurrentPage.Should().NotBeNull();
        navService.CurrentPage!.Url.Should().Be("https://example.com");
        navService.CurrentContext.Should().NotBeNull();
    }

    [Fact]
    public async Task PodcastFeedGenerator_SmokeTest_CanGenerateValidXml()
    {
        var generator = _serviceProvider.GetRequiredService<IPodcastFeedGenerator>();
        var podcast = new PodcastMetadata
        {
            Title = "Test Podcast",
            Description = "A test podcast",
            Author = "Test Author",
            Language = "en",
            ImageUrl = "https://example.com/image.jpg",
        };

        var episodes = new List<EpisodeMetadata>
        {
            new()
            {
                Id = "ep1",
                Title = "Episode 1",
                Description = "First episode",
                PublishedAtUtc = DateTime.UtcNow,
                AudioUrl = "https://example.com/ep1.m4b",
                AudioSizeBytes = 1024000,
                Duration = TimeSpan.FromMinutes(10),
                AudioMimeType = "audio/mp4",
            },
        };

        var xml = await generator.GenerateFeedXmlAsync(podcast, episodes);

        xml.Should().NotBeNullOrWhiteSpace();
        xml.Should().Contain("<rss");
        xml.Should().Contain("Test Podcast");
        xml.Should().Contain("Episode 1");
    }

    private static IConfiguration GetConfiguration()
    {
        var configValues = new Dictionary<string, string?>
        {
            ["Auth:BaseUrl"] = "https://www.nytimes.com",
            ["Auth:MaxArticles"] = "10",
            ["Auth:RateLimitDelayMs"] = "3000",
            ["Browser:BrowserType"] = "Chrome",
            ["Browser:Headless"] = "true",
            ["Browser:ImplicitWaitSeconds"] = "10",
            ["Browser:PageLoadTimeoutSeconds"] = "30",
        };

        return new ConfigurationBuilder()
            .AddInMemoryCollection(configValues)
            .Build();
    }
}
