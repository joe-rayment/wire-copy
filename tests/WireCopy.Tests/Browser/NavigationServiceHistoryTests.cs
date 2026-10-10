// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using WireCopy.Domain.Entities.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Browser;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// workspace-2ojw1: back/forward history must (a) never keep a loading
/// skeleton, (b) remember the reader cursor line, and (c) stay bounded.
/// </summary>
[Trait("Category", "Unit")]
public class NavigationServiceHistoryTests
{
    private readonly NavigationService _sut = new(Substitute.For<ILogger<NavigationService>>());

    private static Page CreatePage(string title, bool skeleton = false)
    {
        var page = Page.Create(
            $"https://example.com/{title}",
            "<html><body>Test</body></html>",
            new PageMetadata { Title = title });
        if (skeleton)
        {
            page.MarkAsSkeleton();
        }

        return page;
    }

    [Fact]
    public void GoBack_FromSkeleton_DoesNotPushSkeletonOntoForwardHistory()
    {
        // Guards:   Esc during a page load must not leave the empty "Loading..." placeholder
        //           on the forward stack (Shift+B would land on a blank page that never loads).
        // Fails if: GoBack pushes the current page onto forward history without checking IsSkeleton.
        // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~NavigationServiceHistoryTests.GoBack_FromSkeleton"
        var real = CreatePage("A");
        var skeleton = CreatePage("B-loading", skeleton: true);
        _sut.NavigateTo(real);
        _sut.NavigateTo(skeleton);

        var result = _sut.GoBack();

        result.Should().BeSameAs(real);
        _sut.CanGoForward.Should().BeFalse("the skeleton is a placeholder, not a page the user visited");
        _sut.GoForward().Should().BeNull();
    }

    [Fact]
    public void NavigateTo_AwayFromSkeleton_DoesNotPushSkeletonOntoBackHistory()
    {
        // Guards:   a loading placeholder never becomes a back-history entry, even if the
        //           user navigates somewhere else before the load finishes.
        // Fails if: NavigateTo pushes the current page onto back history without checking IsSkeleton.
        // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~NavigationServiceHistoryTests.NavigateTo_AwayFromSkeleton"
        var real = CreatePage("A");
        _sut.NavigateTo(real);
        _sut.NavigateTo(CreatePage("B-loading", skeleton: true));

        _sut.NavigateTo(CreatePage("C"));

        _sut.BackHistoryCount.Should().Be(1, "only the real page A is history; the skeleton is not");
        _sut.GoBack().Should().BeSameAs(real);
    }

    [Fact]
    public void GoBackAndForward_RestoreReaderCursorLine()
    {
        // Guards:   going back/forward to an article lands the cursor on the line the user
        //           was reading, not on line 0 (off-screen when the scroll offset is restored).
        // Fails if: history entries stop storing the reader cursor line, or Go* resets it to 0.
        // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~NavigationServiceHistoryTests.GoBackAndForward_RestoreReaderCursorLine"
        var article = CreatePage("article");
        var list = CreatePage("list");
        _sut.NavigateTo(article);
        _sut.SetViewMode(ViewMode.Readable);
        _sut.SetScrollOffset(140);
        _sut.SetReaderCursorLine(150);

        // Esc to the list (a new page), then Shift+B back to the article.
        _sut.NavigateTo(list);
        _sut.SetReaderCursorLine(7);
        _sut.GoBack();

        _sut.CurrentContext.ViewMode.Should().Be(ViewMode.Readable);
        _sut.CurrentContext.ScrollOffset.Should().Be(140);
        _sut.CurrentContext.ReaderCursorLine.Should().Be(150);

        // Forward again to the list restores ITS cursor; back once more restores the article's.
        _sut.GoForward();
        _sut.CurrentContext.ReaderCursorLine.Should().Be(7);
        _sut.GoBack();
        _sut.CurrentContext.ReaderCursorLine.Should().Be(150);
    }

    [Fact]
    public void BackHistory_IsCappedAt100_DroppingTheOldest()
    {
        // Guards:   a long session does not keep every visited page in memory; the oldest
        //           entries fall off and the most recent 100 come back in order.
        // Fails if: the back stack is unbounded, or the cap drops the NEWEST entries instead.
        // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~NavigationServiceHistoryTests.BackHistory_IsCappedAt100"
        var pages = Enumerable.Range(0, 150).Select(i => CreatePage($"p{i}")).ToList();
        foreach (var page in pages)
        {
            _sut.NavigateTo(page);
        }

        _sut.BackHistoryCount.Should().Be(100);

        // Current is p149; the 100 most recent back entries are p148 down to p49.
        for (var expected = 148; expected >= 49; expected--)
        {
            _sut.GoBack().Should().BeSameAs(pages[expected], $"back #{148 - expected + 1} should be p{expected}");
        }

        _sut.CanGoBack.Should().BeFalse();
        _sut.GoBack().Should().BeNull();
        _sut.ForwardHistoryCount.Should().Be(100);
    }

    [Fact]
    public void GetHistoryTitles_AfterCap_ListsCurrentThenNewestBackEntries()
    {
        // Guards:   the history list still works on the capped store: current page first,
        //           then back entries newest-first.
        // Fails if: the capped store enumerates oldest-first, or GetHistoryTitles breaks.
        // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~NavigationServiceHistoryTests.GetHistoryTitles_AfterCap"
        for (var i = 0; i < 120; i++)
        {
            _sut.NavigateTo(CreatePage($"p{i}"));
        }

        var titles = _sut.GetHistoryTitles(3).ToList();

        titles.Should().Equal("→ p119 (current)", "  p118", "  p117", "  p116");
    }
}
