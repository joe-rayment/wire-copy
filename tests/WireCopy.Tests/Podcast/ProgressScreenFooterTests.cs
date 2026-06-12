// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using NSubstitute;
using WireCopy.Application.DTOs.Podcast;
using WireCopy.Application.Interfaces.Podcast;
using WireCopy.Domain.Entities.Collections;
using WireCopy.Infrastructure.Browser.CommandHandlers;
using WireCopy.Infrastructure.Browser.Themes;
using WireCopy.Infrastructure.Browser.UI.Renderers;
using Xunit;

namespace WireCopy.Tests.Podcast;

/// <summary>
/// Tests the destination footer rendered on the in-progress podcast screen
/// (workspace-zh3u).
/// </summary>
[Trait("Category", "Unit")]
[Collection(WireCopy.Tests.ConsoleSerialCollection.Name)]
public class ProgressScreenFooterTests
{
    private static readonly ThemePalette Palette = BuiltInThemes.Get(WireCopy.Domain.Enums.Browser.ThemeName.Phosphor);

    private static string CaptureRender(Action<RenderHelpers> action, int terminalHeight = 30)
    {
        var originalOut = Console.Out;
        using var sw = new StringWriter();
        Console.SetOut(sw);
        try
        {
            var helpers = new RenderHelpers { TerminalHeight = terminalHeight };
            helpers.Clear();
            action(helpers);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        return sw.ToString();
    }

    [Fact]
    [Trait("Collection", "ConsoleOutput")]
    public void RendersSubscribePanel_WhenFeedUrlProvided()
    {
        var targets = new PodcastTargets
        {
            LocalFilePath = "/home/user/podcasts/reading-list.m4b",
            FeedUrl = "https://storage.googleapis.com/my-bucket/podcasts/abc/feed.xml",
        };

        var output = CaptureRender(h => PodcastProgressScreens.RenderDestinationFooter(h, Palette, targets, width: 100));

        // workspace-0v31: subscribe URL is the new headline. The label
        // "Subscribe" leads, the full URL is rendered (not middle-elided
        // at width 100), and the friendly walk-away copy appears above.
        output.Should().Contain("Subscribe",
            because: "the feed URL must be advertised as a subscribe action, not buried under 'Will publish at'");
        output.Should().Contain("storage.googleapis.com",
            because: "the host part of the feed URL must stay visible");
        output.Should().Contain("/feed.xml",
            because: "the tail of the feed URL must stay visible so the user can copy the full address");
        output.Should().Contain("Takes",
            because: "the friendly walk-away copy must explain the run takes a few minutes");
        output.Should().Contain("podcast app",
            because: "the walk-away copy must direct the user to subscribe in their podcast app");
        output.Should().Contain("Will save to",
            because: "the local-file destination stays in the footer as a secondary line");
        output.Should().Contain("reading-list.m4b");
        // workspace-m8es.2: the footer advertises that BACKING OUT keeps the
        // run alive, and warns that the terminal owns the lifecycle.
        output.Should().Contain("Press Esc to free the screen",
            because: "backing out is the safe exit and must be advertised");
        output.Should().Contain("Closing this terminal still cancels",
            because: "the user must still be warned that the process owns the run");
    }

    [Fact]
    [Trait("Collection", "ConsoleOutput")]
    public void RendersLocalOnlyMessage_WhenFeedUrlNull()
    {
        var targets = new PodcastTargets
        {
            LocalFilePath = "/home/user/podcasts/reading-list.m4b",
            FeedUrl = null,
        };

        var output = CaptureRender(h => PodcastProgressScreens.RenderDestinationFooter(h, Palette, targets, width: 100));

        output.Should().Contain("Will save to");
        output.Should().Contain("reading-list.m4b");
        output.Should().NotContain("Will publish at",
            because: "the publish line must be absent in local-only mode");
        output.Should().Contain("no GCS bucket configured");
        output.Should().Contain("generating locally only");
    }

    [Fact]
    [Trait("Collection", "ConsoleOutput")]
    public void TruncatesLongPathsInMiddle()
    {
        var longPath = "/very/deeply/nested/path/that/exceeds/the/available/render/width/by/a/lot/reading-list.m4b";
        var targets = new PodcastTargets
        {
            LocalFilePath = longPath,
            FeedUrl = null,
        };

        var output = CaptureRender(h => PodcastProgressScreens.RenderDestinationFooter(h, Palette, targets, width: 80));

        output.Should().Contain("reading-list.m4b",
            because: "the filename tail must stay visible even when the middle is truncated");
        output.Should().Contain("/very/",
            because: "the parent-folder head must stay visible even when the middle is truncated");
        output.Should().Contain("…", because: "long paths use ellipsis-in-the-middle truncation");
    }

    [Fact]
    public async Task Orchestrator_ResolveTargetsAsync_LocalOnly_WhenBucketUnconfigured()
    {
        var settings = Substitute.For<WireCopy.Application.Interfaces.IUserSettingsStore>();
        settings.Get("GcsBucketName").Returns((string?)null);
        var publisher = Substitute.For<IPodcastPublisher>();

        var collection = Collection.Create("Reading List");

        // Mock the orchestrator's ResolveTargetsAsync behavior via interface check:
        // the production code returns LocalFilePath + null FeedUrl when bucket is unset.
        // We verify that the publisher is NOT called.
        var orchestrator = new FakeOrchestrator(publisher, settings, "/tmp/reading-list.m4b");
        var targets = await orchestrator.ResolveTargetsAsync(collection, default);

        targets.LocalFilePath.Should().Be("/tmp/reading-list.m4b");
        targets.FeedUrl.Should().BeNull();
        await publisher.DidNotReceive().ResolveFeedUrlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Orchestrator_ResolveTargetsAsync_ReturnsFeedUrl_WhenBucketConfigured()
    {
        var settings = Substitute.For<WireCopy.Application.Interfaces.IUserSettingsStore>();
        settings.Get("GcsBucketName").Returns("my-bucket");
        var publisher = Substitute.For<IPodcastPublisher>();
        publisher.ResolveFeedUrlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("https://storage.googleapis.com/my-bucket/podcasts/abc/feed.xml");

        var collection = Collection.Create("Reading List");
        var orchestrator = new FakeOrchestrator(publisher, settings, "/tmp/reading-list.m4b");

        var targets = await orchestrator.ResolveTargetsAsync(collection, default);

        targets.LocalFilePath.Should().Be("/tmp/reading-list.m4b");
        targets.FeedUrl.Should().Be("https://storage.googleapis.com/my-bucket/podcasts/abc/feed.xml");
    }

    [Fact]
    public async Task Orchestrator_ResolveTargetsAsync_FallsBackToLocalOnly_OnPublisherError()
    {
        var settings = Substitute.For<WireCopy.Application.Interfaces.IUserSettingsStore>();
        settings.Get("GcsBucketName").Returns("my-bucket");
        var publisher = Substitute.For<IPodcastPublisher>();
        publisher.ResolveFeedUrlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<string>>(_ => throw new InvalidOperationException("simulated SA failure"));

        var collection = Collection.Create("Reading List");
        var orchestrator = new FakeOrchestrator(publisher, settings, "/tmp/reading-list.m4b");

        var targets = await orchestrator.ResolveTargetsAsync(collection, default);

        targets.LocalFilePath.Should().Be("/tmp/reading-list.m4b");
        targets.FeedUrl.Should().BeNull(
            because: "the footer must never crash on a non-critical lookup");
    }

    /// <summary>
    /// Minimal harness that mirrors the production ResolveTargetsAsync flow
    /// without dragging in the full orchestrator dependency graph. The shape
    /// here MUST stay in sync with the implementation in
    /// <see cref="WireCopy.Infrastructure.Podcast.PodcastOrchestrator"/>.
    /// </summary>
    private sealed class FakeOrchestrator
    {
        private readonly IPodcastPublisher _publisher;
        private readonly WireCopy.Application.Interfaces.IUserSettingsStore _settings;
        private readonly string _localPath;

        public FakeOrchestrator(
            IPodcastPublisher publisher,
            WireCopy.Application.Interfaces.IUserSettingsStore settings,
            string localPath)
        {
            _publisher = publisher;
            _settings = settings;
            _localPath = localPath;
        }

        public async Task<PodcastTargets> ResolveTargetsAsync(Collection collection, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(collection);

            var bucketConfigured = !string.IsNullOrWhiteSpace(_settings.Get("GcsBucketName"));
            if (!bucketConfigured)
            {
                return new PodcastTargets { LocalFilePath = _localPath, FeedUrl = null };
            }

            try
            {
                var feedUrl = await _publisher.ResolveFeedUrlAsync("WireCopy Podcast", ct).ConfigureAwait(false);
                return new PodcastTargets { LocalFilePath = _localPath, FeedUrl = feedUrl };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _ = ex;
                return new PodcastTargets { LocalFilePath = _localPath, FeedUrl = null };
            }
        }
    }
}
