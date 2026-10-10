// Licensed under the MIT License. See LICENSE in the repository root.

using System.Collections;
using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Entities.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Browser.Cache;
using WireCopy.Infrastructure.Configuration;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// workspace-6hc72 — the preload debounce timer callback
/// (<c>OnDebounceElapsed</c>) must (1) never let an exception escape (an
/// unhandled throw on a <see cref="Timer"/> callback terminates the whole
/// process) and (2) never let a slow, older queue build overwrite the queue a
/// newer selection already produced. The tests call the callback directly and
/// park the real 200 ms timer, so nothing here depends on wall-clock timing.
/// </summary>
[Trait("Category", "Unit")]
public class BackgroundPreloadDebounceTests : IDisposable
{
    private const string Origin = "https://example.com";

    private readonly CapturingLogger _logger = new();
    private readonly BackgroundPreloadService _service;

    public BackgroundPreloadDebounceTests()
    {
        _service = new BackgroundPreloadService(
            Substitute.For<IPageCache>(),
            Substitute.For<IIdleDetector>(),
            new HttpClient(),
            new CacheConfiguration(),
            _logger);
    }

    public void Dispose()
    {
        _service.Dispose();
    }

    // Guards:   a link-tree queue build that throws inside the debounce callback is swallowed and logged as a warning, and the previous queue stays in place (an escaped exception kills the app).
    // Fails if: OnDebounceElapsed has no try/catch around the build, so the exception propagates out of the callback.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~OnDebounceElapsed_LinkTreeBuildThrows"
    [Fact]
    public void OnDebounceElapsed_LinkTreeBuildThrows_DoesNotThrowAndKeepsPreviousQueue()
    {
        var previous = new List<BackgroundPreloadService.PreloadItem> { new("https://example.com/previous", 0) };
        SetQueue(previous);

        var nodes = new HookedList<LinkNode>(ContentNodes("https://example.com/a1"));
        _service.NotifySelectionChanged(0, nodes, Origin);
        StopDebounceTimer();
        nodes.BeforeAccess = () => throw new InvalidOperationException("simulated visible-node enumeration failure");

        var act = () => _service.OnDebounceElapsed(null);

        act.Should().NotThrow("an exception escaping a Timer callback terminates the process");
        GetQueue().Should().BeSameAs(previous, "a failed rebuild must leave the previous queue untouched");
        _logger.Entries.Should().Contain(
            e => e.Level == LogLevel.Warning && e.Message.Contains("simulated visible-node enumeration failure"),
            "the failure must be logged as a warning with the cause");
    }

    // Guards:   a collection queue build that throws inside the debounce callback is swallowed and the previous queue stays in place.
    // Fails if: the collection branch of OnDebounceElapsed is left outside the try/catch.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~OnDebounceElapsed_CollectionBuildThrows"
    [Fact]
    public void OnDebounceElapsed_CollectionBuildThrows_DoesNotThrowAndKeepsPreviousQueue()
    {
        var previous = new List<BackgroundPreloadService.PreloadItem> { new("https://example.com/previous", 0) };
        SetQueue(previous);

        var urls = new HookedList<string>(["https://col.example.com/1"]);
        _service.NotifyCollectionChanged(0, urls);
        StopDebounceTimer();
        urls.BeforeAccess = () => throw new InvalidOperationException("simulated collection enumeration failure");

        var act = () => _service.OnDebounceElapsed(null);

        act.Should().NotThrow("an exception escaping a Timer callback terminates the process");
        GetQueue().Should().BeSameAs(previous, "a failed rebuild must leave the previous queue untouched");
        _logger.Entries.Should().Contain(e => e.Level == LogLevel.Warning);
    }

    // Guards:   when an older link-tree build finishes AFTER a newer selection already produced its queue, the older (stale) result is dropped instead of overwriting the newer queue.
    // Fails if: OnDebounceElapsed assigns _queue unconditionally (no generation check), so the slow older callback wins.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~OnDebounceElapsed_OlderLinkTreeBuildFinishingLast"
    [Fact]
    public async Task OnDebounceElapsed_OlderLinkTreeBuildFinishingLast_DoesNotOverwriteNewerQueue()
    {
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);

        // Selection #1: its build parks mid-way (inside the visible-node list) until released.
        var olderNodes = new HookedList<LinkNode>(ContentNodes("https://example.com/older1"));
        _service.NotifySelectionChanged(0, olderNodes, Origin);
        StopDebounceTimer();
        olderNodes.BeforeAccess = () => ParkUntilReleased(entered, release);
        var older = Task.Run(() => _service.OnDebounceElapsed(null));
        entered.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue("the older callback must be mid-build before the newer selection arrives");

        // Selection #2 arrives and its callback completes while #1 is still parked.
        _service.NotifySelectionChanged(0, ContentNodes("https://example.com/newer1", "https://example.com/newer2"), Origin);
        StopDebounceTimer();
        _service.OnDebounceElapsed(null);
        GetQueue().Select(i => i.Url).Should().BeEquivalentTo(["https://example.com/newer1", "https://example.com/newer2"]);

        // Now the older build finishes last.
        release.Set();
        await older.WaitAsync(TimeSpan.FromSeconds(10));

        GetQueue().Select(i => i.Url).Should().BeEquivalentTo(
            ["https://example.com/newer1", "https://example.com/newer2"],
            "a stale older build must not overwrite the queue built for the newer selection");
    }

    // Guards:   the same stale-overwrite protection holds for the collection path (older collection build finishing after a newer one).
    // Fails if: the collection branch of OnDebounceElapsed assigns _queue unconditionally.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~OnDebounceElapsed_OlderCollectionBuildFinishingLast"
    [Fact]
    public async Task OnDebounceElapsed_OlderCollectionBuildFinishingLast_DoesNotOverwriteNewerQueue()
    {
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);

        var olderUrls = new HookedList<string>(["https://col.example.com/older1"]);
        _service.NotifyCollectionChanged(0, olderUrls);
        StopDebounceTimer();
        olderUrls.BeforeAccess = () => ParkUntilReleased(entered, release);
        var older = Task.Run(() => _service.OnDebounceElapsed(null));
        entered.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue("the older callback must be mid-build before the newer selection arrives");

        _service.NotifyCollectionChanged(0, ["https://col.example.com/newer1", "https://col.example.com/newer2"]);
        StopDebounceTimer();
        _service.OnDebounceElapsed(null);
        GetQueue().Select(i => i.Url).Should().BeEquivalentTo(["https://col.example.com/newer1", "https://col.example.com/newer2"]);

        release.Set();
        await older.WaitAsync(TimeSpan.FromSeconds(10));

        GetQueue().Select(i => i.Url).Should().BeEquivalentTo(
            ["https://col.example.com/newer1", "https://col.example.com/newer2"],
            "a stale older collection build must not overwrite the newer queue");
    }

    // Guards:   re-running the callback for an unchanged selection (what RequestQueueRebuild does) still publishes its queue; the staleness check only drops builds superseded by a NEWER selection.
    // Fails if: the generation check is made too strict, e.g. the snapshot is "consumed" so a second run for the same selection is discarded.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~OnDebounceElapsed_RerunForSameSelection"
    [Fact]
    public void OnDebounceElapsed_RerunForSameSelection_StillPublishesQueue()
    {
        _service.NotifySelectionChanged(0, ContentNodes("https://example.com/a1", "https://example.com/a2"), Origin);
        StopDebounceTimer();

        _service.OnDebounceElapsed(null);
        SetQueue([]);
        _service.OnDebounceElapsed(null);

        GetQueue().Select(i => i.Url).Should().BeEquivalentTo(["https://example.com/a1", "https://example.com/a2"]);
    }

    private static void ParkUntilReleased(ManualResetEventSlim entered, ManualResetEventSlim release)
    {
        entered.Set();
        if (!release.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new TimeoutException("test never released the parked build");
        }
    }

    private static List<LinkNode> ContentNodes(params string[] urls)
    {
        var root = LinkNode.CreateRoot();
        foreach (var url in urls)
        {
            root.AddChild(new LinkInfo
            {
                Url = url,
                DisplayText = $"Article at {url}",
                Type = LinkType.Content,
                ImportanceScore = 50,
            });
        }

        return root.Children.ToList();
    }

    private static FieldInfo Field(string name) =>
        typeof(BackgroundPreloadService).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!;

    private List<BackgroundPreloadService.PreloadItem> GetQueue() =>
        (List<BackgroundPreloadService.PreloadItem>)Field("_queue").GetValue(_service)!;

    private void SetQueue(List<BackgroundPreloadService.PreloadItem> queue) =>
        Field("_queue").SetValue(_service, queue);

    // NotifySelectionChanged/NotifyCollectionChanged arm a real 200 ms timer; park it so
    // only the test's explicit OnDebounceElapsed calls run the callback.
    private void StopDebounceTimer() =>
        ((Timer)Field("_debounceTimer").GetValue(_service)!).Change(Timeout.Infinite, Timeout.Infinite);

    /// <summary>Read-only list whose Count/indexer run a hook, so a test can make a build fail or park mid-build.</summary>
    private sealed class HookedList<T>(IReadOnlyList<T> inner) : IReadOnlyList<T>
    {
        public Action? BeforeAccess { get; set; }

        public int Count
        {
            get
            {
                BeforeAccess?.Invoke();
                return inner.Count;
            }
        }

        public T this[int index]
        {
            get
            {
                BeforeAccess?.Invoke();
                return inner[index];
            }
        }

        public IEnumerator<T> GetEnumerator() => inner.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class CapturingLogger : ILogger<BackgroundPreloadService>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Entries)
            {
                Entries.Add((logLevel, formatter(state, exception) + (exception == null ? string.Empty : " | " + exception.Message)));
            }
        }
    }
}
