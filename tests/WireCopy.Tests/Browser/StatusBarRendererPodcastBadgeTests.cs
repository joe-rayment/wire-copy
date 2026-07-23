// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using NSubstitute;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.DTOs.Podcast;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Browser.Themes;
using WireCopy.Infrastructure.Browser.UI.Renderers;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// Tests for the podcast detach badge rendered by <see cref="StatusBarRenderer"/>.
/// Phase D of workspace-vkhr — surfaces an in-flight podcast generation in the
/// status bar so the user remembers there's a run happening while the modal
/// is closed.
/// </summary>
[Trait("Category", "Unit")]
[Collection(ConsoleSerialCollection.Name)]
public class StatusBarRendererPodcastBadgeTests
{
    private static string CaptureConsoleOutput(Action action)
    {
        var originalOut = Console.Out;
        try
        {
            using var sw = new StringWriter();
            Console.SetOut(sw);
            action();
            return sw.ToString();
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    private static StatusBarRenderer CreateRenderer(IPodcastBackgroundJobManager? manager)
    {
        var themeProvider = Substitute.For<IThemeProvider>();
        themeProvider.CurrentTheme.Returns(ThemeName.Phosphor);
        return new StatusBarRenderer(new RenderHelpers(), themeProvider, manager);
    }

    [Fact]
    public void PodcastBadge_HiddenWhenNoActiveJob()
    {
        var manager = Substitute.For<IPodcastBackgroundJobManager>();
        manager.HasActiveJob.Returns(false);
        var statusBar = CreateRenderer(manager);
        var context = new NavigationContext { ViewMode = ViewMode.Hierarchical };

        var output = CaptureConsoleOutput(() =>
            statusBar.RenderStatusBar(context, ViewMode.Hierarchical, terminalWidth: 200));

        output.Should().NotContain("\u266b",
            "the badge must not render the headphones glyph when no podcast job is active");
        output.Should().NotContain("Generating",
            "no 'Generating' copy should appear in the right-hand status content");
    }

    [Fact]
    public void PodcastBadge_HiddenWhenManagerIsNull()
    {
        // The legacy no-DI test path constructs the renderer without a manager.
        // The badge must silently no-op rather than crash.
        var statusBar = CreateRenderer(manager: null);
        var context = new NavigationContext { ViewMode = ViewMode.Hierarchical };

        var output = CaptureConsoleOutput(() =>
            statusBar.RenderStatusBar(context, ViewMode.Hierarchical, terminalWidth: 200));

        output.Should().NotContain("\u266b");
        output.Should().NotContain("Generating");
    }

    [Fact]
    public void PodcastBadge_VisibleWhenManagerHasActiveJob()
    {
        var manager = Substitute.For<IPodcastBackgroundJobManager>();
        manager.HasActiveJob.Returns(true);
        manager.LastSnapshot.Returns(new PodcastProgress
        {
            Phase = PodcastPhase.GeneratingAudio,
            PercentComplete = 67,
            CurrentArticle = 3,
            TotalArticles = 5,
        });
        var statusBar = CreateRenderer(manager);
        var context = new NavigationContext { ViewMode = ViewMode.Hierarchical };

        var output = CaptureConsoleOutput(() =>
            statusBar.RenderStatusBar(context, ViewMode.Hierarchical, terminalWidth: 200));

        output.Should().Contain("\u266b",
            "the headphones glyph signals an in-flight podcast at a glance");
        output.Should().Contain("Generating 67%",
            "the percent surfaces from the manager's last snapshot");
        output.Should().Contain("Shift+P",
            "the badge tells the user how to restore the modal");
        output.Should().Contain(":restore");
    }

    [Fact]
    public void PodcastBadge_RendersZeroPercent_WhenSnapshotMissing()
    {
        // A job that has just been registered may have no snapshot yet — the
        // badge should still surface so the user has the restore affordance.
        var manager = Substitute.For<IPodcastBackgroundJobManager>();
        manager.HasActiveJob.Returns(true);
        manager.LastSnapshot.Returns((PodcastProgress?)null);
        var statusBar = CreateRenderer(manager);
        var context = new NavigationContext { ViewMode = ViewMode.Hierarchical };

        var output = CaptureConsoleOutput(() =>
            statusBar.RenderStatusBar(context, ViewMode.Hierarchical, terminalWidth: 200));

        output.Should().Contain("Generating 0%");
        output.Should().Contain("Shift+P");
    }

    [Fact]
    public void PodcastBadge_ClampsPercentToZeroToHundred()
    {
        var manager = Substitute.For<IPodcastBackgroundJobManager>();
        manager.HasActiveJob.Returns(true);
        manager.LastSnapshot.Returns(new PodcastProgress
        {
            Phase = PodcastPhase.Publishing,
            PercentComplete = 150,
        });
        var statusBar = CreateRenderer(manager);
        var context = new NavigationContext { ViewMode = ViewMode.Hierarchical };

        var output = CaptureConsoleOutput(() =>
            statusBar.RenderStatusBar(context, ViewMode.Hierarchical, terminalWidth: 200));

        output.Should().Contain("Generating 100%",
            "percent is clamped so a runaway PercentComplete value can't render '150%'");
    }
}
