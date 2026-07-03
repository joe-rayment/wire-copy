// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using NSubstitute;
using WireCopy.Application.DTOs.Scheduling;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Scheduling;
using Xunit;

namespace WireCopy.Tests.Scheduling;

[Trait("Category", "Unit")]
public class UnattendedSectionLoadAdapterTests
{
    private readonly IPreloadService _preload = Substitute.For<IPreloadService>();
    private readonly ILinkExtractor _extractor = Substitute.For<ILinkExtractor>();
    private readonly IHierarchyConfigStore _configStore = Substitute.For<IHierarchyConfigStore>();

    private UnattendedSectionLoadAdapter NewAdapter() => new(_preload, _extractor, _configStore);

    [Fact]
    public async Task OkLoad_ExtractsLinks_AndMatchesConfig_ViaTheRenderedPath()
    {
        _preload.LoadRenderedHtmlAsync("https://nyt.com/", Arg.Any<CancellationToken>())
            .Returns(new RenderedLoad { Outcome = LoadOutcome.Ok, Html = "<html>..</html>", FinalUrl = "https://nyt.com/" });
        var links = new List<LinkInfo> { new() { Url = "https://nyt.com/a", DisplayText = "A", Type = LinkType.Content, ImportanceScore = 70 } };
        _extractor.ExtractLinksAsync("<html>..</html>", "https://nyt.com/", Arg.Any<CancellationToken>()).Returns(links);
        var cfg = new SiteHierarchyConfig { Domain = "nyt.com", UrlPattern = "^x$", Sections = new(), CreatedAt = DateTime.UtcNow, ModelVersion = "m" };
        _configStore.GetConfigAsync("https://nyt.com/").Returns(cfg);

        var result = await NewAdapter().LoadLinksAndConfigAsync("https://nyt.com/");

        result.Outcome.Should().Be(LoadOutcome.Ok);
        result.Links.Should().HaveCount(1);
        result.Config.Should().BeSameAs(cfg);
        // The RENDERED load path was used (not a bare fetch).
        await _preload.Received(1).LoadRenderedHtmlAsync("https://nyt.com/", Arg.Any<CancellationToken>());
        await _extractor.Received(1).ExtractLinksAsync("<html>..</html>", "https://nyt.com/", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BlockedLoad_ReturnsBlocked_WithoutExtracting()
    {
        _preload.LoadRenderedHtmlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new RenderedLoad { Outcome = LoadOutcome.Blocked, FinalUrl = "x" });

        var result = await NewAdapter().LoadLinksAndConfigAsync("https://paywalled.com/");

        result.Outcome.Should().Be(LoadOutcome.Blocked);
        result.Links.Should().BeEmpty();
        result.Config.Should().BeNull();
        await _extractor.DidNotReceive().ExtractLinksAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LoadFailed_ReturnsLoadFailed_WithoutExtracting()
    {
        _preload.LoadRenderedHtmlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new RenderedLoad { Outcome = LoadOutcome.LoadFailed, FinalUrl = "x" });

        var result = await NewAdapter().LoadLinksAndConfigAsync("https://down.com/");

        result.Outcome.Should().Be(LoadOutcome.LoadFailed);
        result.Links.Should().BeEmpty();
        await _extractor.DidNotReceive().ExtractLinksAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Adapter_DependsOnNoForegroundOrNavigationService()
    {
        // By construction it only takes IPreloadService + ILinkExtractor +
        // IHierarchyConfigStore — never NavigationService or IPageAccessQueue.
        var paramTypes = typeof(UnattendedSectionLoadAdapter)
            .GetConstructors().Single().GetParameters().Select(p => p.ParameterType.Name).ToList();
        paramTypes.Should().NotContain(n => n.Contains("NavigationService", StringComparison.Ordinal));
        paramTypes.Should().NotContain(n => n.Contains("PageAccessQueue", StringComparison.Ordinal));
        // workspace-frpl.11 (B8): IAutoCookieRefresher is an abstraction — the adapter
        // still names NO foreground/NavigationService type directly.
        paramTypes.Should().BeEquivalentTo(new[] { "IPreloadService", "ILinkExtractor", "IHierarchyConfigStore", "IAutoCookieRefresher" });
    }
}
