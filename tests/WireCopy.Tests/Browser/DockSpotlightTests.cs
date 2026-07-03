// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using WireCopy.Domain.Entities.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Browser;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// Unit tests for the concert-view dock spotlight: target resolution from view
/// state, the latest-wins request pump, and the not-docked fast bail.
/// The page-side behavior (overlay + scroll) is covered by the live Xvfb test
/// in <see cref="DockSpotlightIntegrationTests"/>.
/// </summary>
public class DockSpotlightTests
{
    // ---- ResolveTarget: what should light up, what should clear ----

    [Fact]
    public void ResolveTarget_HierarchicalWithLinkSelected_ReturnsTarget()
    {
        var page = CreatePage("https://news.example.com/");
        var tree = page.LinkTree!;
        var link = tree.GetVisibleNodes().First(n => !n.IsGroupHeader);
        tree.SelectNodeById(link.Id);

        var target = DockSpotlight.ResolveTarget(ViewMode.Hierarchical, page, tree);

        target.Should().NotBeNull();
        target!.Value.PageUrl.Should().Be("https://news.example.com/");
        target.Value.LinkUrl.Should().Be(link.Link.Url);
        target.Value.DisplayText.Should().Be(link.Link.DisplayText);
    }

    [Fact]
    public void ResolveTarget_GroupHeaderSelected_FollowsPageOnly()
    {
        // workspace-exbz: no concrete story selected — the live window must still keep
        // FOLLOWING the page the terminal shows (cache hits never navigate it), it just
        // draws no highlight box.
        var page = CreatePage("https://news.example.com/");
        var tree = page.LinkTree!;
        var header = tree.GetVisibleNodes().First(n => n.IsGroupHeader);
        tree.SelectNodeById(header.Id);

        var target = DockSpotlight.ResolveTarget(ViewMode.Hierarchical, page, tree);

        target.Should().NotBeNull();
        target!.Value.FollowPageOnly.Should().BeTrue();
        target.Value.PageUrl.Should().Be("https://news.example.com/");
    }

    [Fact]
    public void ResolveTarget_NonWebPageUrl_ReturnsNull()
    {
        // Follow-only targets are meaningless for non-web URLs (launcher, skeleton,
        // data:) — navigating the live window there would show garbage.
        var page = CreatePage("file:///tmp/snapshot.html");
        var tree = page.LinkTree!;
        var header = tree.GetVisibleNodes().First(n => n.IsGroupHeader);
        tree.SelectNodeById(header.Id);

        DockSpotlight.ResolveTarget(ViewMode.Hierarchical, page, tree).Should().BeNull();
        DockSpotlight.ResolveTarget(ViewMode.Readable, page, tree).Should().BeNull();
    }

    [Theory]
    [InlineData(ViewMode.Launcher)]
    [InlineData(ViewMode.CollectionList)]
    [InlineData(ViewMode.CollectionItems)]
    public void ResolveTarget_NonFollowingViews_ReturnNull(ViewMode viewMode)
    {
        var page = CreatePage("https://news.example.com/");

        DockSpotlight.ResolveTarget(viewMode, page, page.LinkTree).Should().BeNull();
    }

    [Fact]
    public void ResolveTarget_ReaderView_ReturnsFollowOnlyTarget()
    {
        // workspace-nqqs: reader view has no anchor to highlight, but the live page should
        // still follow the article being read (the lens). The target carries the page url
        // as both page and link and is flagged follow-only so no highlight box is drawn.
        var page = CreatePage("https://news.example.com/article");

        var target = DockSpotlight.ResolveTarget(ViewMode.Readable, page, page.LinkTree);

        target.Should().NotBeNull();
        target!.Value.FollowPageOnly.Should().BeTrue();
        target.Value.PageUrl.Should().Be("https://news.example.com/article");
    }

    [Fact]
    public void ResolveTarget_NoPage_ReturnsNull()
    {
        DockSpotlight.ResolveTarget(ViewMode.Hierarchical, page: null, tree: null).Should().BeNull();
    }

    // ---- Request pump: not-docked bail and coalescing ----

    [Fact]
    public async Task RequestSync_WhenNotDocked_NeverTouchesTheLensPage()
    {
        var session = Substitute.For<IBrowserSession>();
        session.IsDocked.Returns(false);
        session.HasActiveBrowser.Returns(true);

        await using var spotlight = CreateSpotlight(session);
        spotlight.RequestSync(new SpotlightTarget("https://a/", "https://a/1", "one"));
        await Task.Delay(200);

        await session.DidNotReceive().GetLensPageAsync();
    }

    [Fact]
    public async Task RequestClear_WhenNothingApplied_NeverTouchesTheLensPage()
    {
        var session = Substitute.For<IBrowserSession>();
        session.IsDocked.Returns(true);
        session.HasActiveBrowser.Returns(true);

        await using var spotlight = CreateSpotlight(session);
        spotlight.RequestClear();
        await Task.Delay(200);

        await session.DidNotReceive().GetLensPageAsync();
    }

    [Fact]
    public async Task RequestSync_Burst_CoalescesToLatestTarget()
    {
        var session = Substitute.For<IBrowserSession>();
        session.IsDocked.Returns(true);
        session.HasActiveBrowser.Returns(true);

        // A lens lookup whose first call blocks until released lets the test
        // pile up a burst behind an in-flight sync, then observe what drains.
        var firstAcquireEntered = new TaskCompletionSource();
        var releaseFirstAcquire = new TaskCompletionSource();
        var acquisitions = 0;
        session.GetLensPageAsync().Returns(async _ =>
        {
            var n = Interlocked.Increment(ref acquisitions);
            if (n == 1)
            {
                firstAcquireEntered.SetResult();
                await releaseFirstAcquire.Task;
            }

            // Throwing keeps the pump's error path local — the test only cares
            // how many syncs were attempted, not what they did to a page.
            throw new InvalidOperationException("no page in unit test");
        });

        await using var spotlight = CreateSpotlight(session);

        spotlight.RequestSync(new SpotlightTarget("https://a/", "https://a/1", "one"));
        await firstAcquireEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // Burst arrives while sync #1 is stuck in GetLensPageAsync.
        spotlight.RequestSync(new SpotlightTarget("https://a/", "https://a/2", "two"));
        spotlight.RequestSync(new SpotlightTarget("https://a/", "https://a/3", "three"));
        spotlight.RequestSync(new SpotlightTarget("https://a/", "https://a/4", "four"));
        releaseFirstAcquire.SetResult();

        // Drain: the burst must collapse into exactly ONE follow-up sync.
        await WaitForAsync(() => Volatile.Read(ref acquisitions) >= 2);

        // Quiescence barrier for the negative assertion: the pump's drain loop
        // doesn't observe cancellation, so DisposeAsync only returns once every
        // taken-or-pending request has been processed and the pump task has
        // exited. After it returns the count is final — a leaked third sync
        // would already have been counted. (No wall-clock settle: under full-
        // suite load a fixed delay can both miss a late extra sync and isn't
        // long enough to prove there wasn't one.)
        await spotlight.DisposeAsync();
        Volatile.Read(ref acquisitions).Should().Be(2, "three queued requests must coalesce into one");
    }

    [Fact]
    public async Task MissHint_ReArmsAfterASuccessfulSync_OnTheSamePage()
    {
        // workspace-2hfr (minor 2): the not-found hint is throttled to once per page,
        // but a SUCCESSFUL sync is a state change — the next genuine miss on the same
        // page must be reported again, not silently swallowed by the stale throttle.
        var session = Substitute.For<IBrowserSession>();
        session.IsDocked.Returns(true);
        session.HasActiveBrowser.Returns(true);

        var page = Substitute.For<Microsoft.Playwright.IPage>();
        page.Url.Returns("https://a/");
        session.GetLensPageAsync().Returns(Task.FromResult<Microsoft.Playwright.IPage?>(page));

        // Miss, then success, then miss again. (The rescue-expansion probes return
        // NSubstitute's default null and therefore change nothing.)
        var syncCalls = 0;
        page.EvaluateAsync<string>(SpotlightScript.Sync, Arg.Any<object?>())
            .Returns(_ => Interlocked.Increment(ref syncCalls) switch
            {
                2 => "ok",
                _ => "not-found",
            });

        var hints = new List<string>();
        await using var spotlight = CreateSpotlight(session);
        spotlight.StatusMessageSink = message =>
        {
            lock (hints)
            {
                hints.Add(message);
            }
        };

        spotlight.RequestSync(new SpotlightTarget("https://a/", "https://a/1", "one"));
        await WaitForAsync(() =>
        {
            lock (hints)
            {
                return hints.Count == 1;
            }
        });

        spotlight.RequestSync(new SpotlightTarget("https://a/", "https://a/2", "two"));
        await WaitForAsync(() => Volatile.Read(ref syncCalls) >= 2);

        spotlight.RequestSync(new SpotlightTarget("https://a/", "https://a/3", "three"));
        await WaitForAsync(() =>
        {
            lock (hints)
            {
                return hints.Count == 2;
            }
        });

        lock (hints)
        {
            hints.Should().HaveCount(2, "the success between the two misses re-arms the once-per-page throttle");
            hints.Should().AllSatisfy(h => h.Should().Contain("isn't visible on the live page"));
        }
    }

    [Fact]
    public async Task DisposeAsync_WithIdlePump_CompletesQuickly()
    {
        var session = Substitute.For<IBrowserSession>();
        var spotlight = CreateSpotlight(session);

        var dispose = spotlight.DisposeAsync().AsTask();
        var finished = await Task.WhenAny(dispose, Task.Delay(TimeSpan.FromSeconds(5)));

        finished.Should().BeSameAs(dispose, "an idle pump must not block disposal");
    }

    private static DockSpotlight CreateSpotlight(IBrowserSession session)
        => new(session, NullLogger<DockSpotlight>.Instance);

    private static async Task WaitForAsync(Func<bool> condition)
    {
        // Generous deadline: under full-suite parallel load the pump's Task.Run
        // and mock continuations can be starved for seconds. The wait exits as
        // soon as the condition holds, so passing runs never pay this.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        condition().Should().BeTrue("condition should be reached within the timeout");
    }

    private static Page CreatePage(string url)
    {
        // BuildWithGroups so the tree contains both plain links and a
        // collapsible group header (Navigation) for the header test case.
        var grouped = new Dictionary<LinkType, List<LinkInfo>>
        {
            [LinkType.Content] = new()
            {
                new LinkInfo
                {
                    Url = "https://news.example.com/story-1",
                    DisplayText = "Story one",
                    Type = LinkType.Content,
                    ImportanceScore = 80,
                },
                new LinkInfo
                {
                    Url = "https://news.example.com/story-2",
                    DisplayText = "Story two",
                    Type = LinkType.Content,
                    ImportanceScore = 80,
                },
            },
            [LinkType.Navigation] = new()
            {
                new LinkInfo
                {
                    Url = "https://news.example.com/sections",
                    DisplayText = "Sections",
                    Type = LinkType.Navigation,
                    ImportanceScore = 20,
                },
            },
        };

        var tree = NavigationTree.BuildWithGroups(grouped);
        var page = Page.Create(url, "<html></html>", new PageMetadata { Title = "Test page" });
        page.SetLinkTree(tree);
        return page;
    }
}
