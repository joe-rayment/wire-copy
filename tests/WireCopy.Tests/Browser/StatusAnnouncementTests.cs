// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Browser;
using WireCopy.Infrastructure.Browser.UI.Renderers;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// workspace-wef6.4 — the Announce() API: state changes announce themselves
/// with their keys in the Transient channel, the SetStatusMessage shim keeps
/// the ~200 legacy call sites working, and TTLs are clock-based (injected
/// TimeProvider) so announcements survive any number of re-renders inside
/// their window.
/// </summary>
[Trait("Category", "Unit")]
public class StatusAnnouncementTests
{
    private static readonly DateTime T0 = new(2026, 6, 11, 12, 0, 0, DateTimeKind.Utc);

    private static (NavigationService Service, FakeTimeProvider Clock) MakeService()
    {
        var clock = new FakeTimeProvider(T0);
        var service = new NavigationService(Substitute.For<ILogger<NavigationService>>(), clock);
        return (service, clock);
    }

    [Fact]
    public void Announce_PopulatesActiveAnnouncement_WithGlyphTextAndKeys()
    {
        var (service, _) = MakeService();

        service.Announce(
            "▶",
            "Speed reading 350 WPM",
            new[] { new StatusKeyHint("<", "slower"), new StatusKeyHint(">", "faster"), new StatusKeyHint("f", "stop") },
            TimeSpan.FromSeconds(4),
            "▶350");

        var announcement = service.CurrentContext.ActiveAnnouncement;
        announcement.Should().NotBeNull();
        announcement!.Glyph.Should().Be("▶");
        announcement.Text.Should().Be("Speed reading 350 WPM");
        announcement.Keys.Should().HaveCount(3);
        announcement.ShortText.Should().Be("▶350");
    }

    [Fact]
    public void Announce_SurvivesRerenders_UntilClockBasedTtl()
    {
        var (service, clock) = MakeService();
        service.Announce("▶", "Speed reading 350 WPM", ttl: TimeSpan.FromSeconds(4));

        // Many context reads (= re-renders) inside the window — all must see it.
        for (var i = 0; i < 30; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            service.CurrentContext.ActiveAnnouncement.Should().NotBeNull(
                $"the announcement must survive re-render #{i} inside its 4s TTL");
        }

        clock.Advance(TimeSpan.FromSeconds(2));
        service.CurrentContext.ActiveAnnouncement.Should().BeNull("the TTL elapsed");
    }

    [Fact]
    public void SetStatusMessage_Shim_RoutesIntoAnnouncement()
    {
        var (service, clock) = MakeService();

        service.SetStatusMessage("Theme: Phosphor");

        var context = service.CurrentContext;
        context.StatusMessage.Should().Be("Theme: Phosphor", "the back-compat property mirrors the announcement");
        context.ActiveAnnouncement.Should().NotBeNull();
        context.ActiveAnnouncement!.Text.Should().Be("Theme: Phosphor");

        clock.Advance(TimeSpan.FromSeconds(3.5));
        service.CurrentContext.StatusMessage.Should().BeNull("the default 3s TTL applies");
    }

    [Fact]
    public void SetStatusMessage_WithDuration_KeepsCustomTtl()
    {
        var (service, clock) = MakeService();

        service.SetStatusMessage("Saved schedule", TimeSpan.FromSeconds(5));

        clock.Advance(TimeSpan.FromSeconds(4));
        service.CurrentContext.StatusMessage.Should().NotBeNull("4s < the custom 5s TTL");
        clock.Advance(TimeSpan.FromSeconds(1.5));
        service.CurrentContext.StatusMessage.Should().BeNull();
    }

    [Fact]
    public void ClearStatusMessage_DropsTheAnnouncement()
    {
        var (service, _) = MakeService();
        service.Announce("✓", "Saved (12)");

        service.ClearStatusMessage();

        service.CurrentContext.ActiveAnnouncement.Should().BeNull();
        service.CurrentContext.StatusMessage.Should().BeNull();
    }

    // ---- Rendering: the fixed transient region with key hints ----

    [Fact]
    public void ComposedLine_RendersAnnouncementWithKeyHints()
    {
        var context = new NavigationContext
        {
            ViewMode = ViewMode.Readable,
            ActiveAnnouncement = new StatusAnnouncement
            {
                Glyph = "▶",
                Text = "Speed reading 350 WPM",
                Keys = new[]
                {
                    new StatusKeyHint("<", "slower"),
                    new StatusKeyHint(">", "faster"),
                    new StatusKeyHint("f", "stop"),
                },
                ShortText = "▶350",
            },
        };

        var model = StatusBarRenderer.ComposeStatusLine(context, ViewMode.Readable, 160);

        model.PlainText.Should().Contain("▶ Speed reading 350 WPM — <:slower >:faster f:stop",
            "the announcement renders its full copy with inline key hints when space allows");
    }

    [Fact]
    public void ComposedLine_DegradesAnnouncementToShortForm_WhenSqueezed()
    {
        var context = new NavigationContext
        {
            ViewMode = ViewMode.Readable,
            ActiveAnnouncement = new StatusAnnouncement
            {
                Glyph = "▶",
                Text = "Speed reading 350 WPM",
                Keys = new[]
                {
                    new StatusKeyHint("<", "slower"),
                    new StatusKeyHint(">", "faster"),
                    new StatusKeyHint("f", "stop"),
                },
                ShortText = "▶350",
            },
        };

        var model = StatusBarRenderer.ComposeStatusLine(context, ViewMode.Readable, 22);

        model.PlainText.Should().Contain("▶350", "the squeezed line keeps the compact form");
        model.PlainText.Should().NotContain("slower", "key hints are the first thing to go");
        RenderHelpers.GetDisplayWidth(model.PlainText).Should().BeLessThanOrEqualTo(21);
    }

    [Fact]
    public void ComposedLine_AnnouncementNeverSilentlyDropped_AcrossWidths()
    {
        var context = new NavigationContext
        {
            ViewMode = ViewMode.Readable,
            ActiveAnnouncement = new StatusAnnouncement
            {
                Glyph = "✓",
                Text = "Saved (12)",
                Keys = new[] { new StatusKeyHint("c", "list") },
                ShortText = "✓12",
            },
        };

        for (var width = 40; width <= 200; width++)
        {
            var model = StatusBarRenderer.ComposeStatusLine(context, ViewMode.Readable, width);
            model.PlainText.Should().MatchRegex("✓",
                $"the active transient must be visible at width {width} — this is the bug class the epic kills");
        }
    }

    [Fact]
    public void PlainStatusMessage_StillRenders_ForLegacyContexts()
    {
        // Tests and legacy paths construct NavigationContext directly with
        // only StatusMessage set; the renderer keeps honoring it.
        var context = new NavigationContext
        {
            ViewMode = ViewMode.Hierarchical,
            StatusMessage = "325 WPM",
        };

        var model = StatusBarRenderer.ComposeStatusLine(context, ViewMode.Hierarchical, 120);

        model.PlainText.Should().Contain("325 WPM");
    }
}
