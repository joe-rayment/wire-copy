// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Infrastructure.Browser.Themes;
using WireCopy.Infrastructure.Browser.UI.Renderers;
using WireCopy.Infrastructure.Browser.UI.StatusLine;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// workspace-wef6 B1 — property tests for <see cref="StatusComposer"/>:
/// composed width never exceeds the budget at widths 40..200, alerts are
/// visible at every width, degradation order is stable (hints absorb squeeze
/// first; ambient degrades/drops before transients; alerts never drop), and
/// transient TTLs are clock-based.
/// </summary>
[Trait("Category", "Unit")]
public class StatusComposerTests
{
    private static readonly DateTime T0 = new(2026, 6, 11, 12, 0, 0, DateTimeKind.Utc);

    private static StatusItem Alert(string longText, string shortText) => new()
    {
        Channel = StatusChannel.Alert,
        Variants = new[]
        {
            new[] { new StatusSegment(longText, StatusStyle.Warning) },
            new[] { new StatusSegment(shortText, StatusStyle.Warning) },
        },
    };

    private static StatusItem Transient(string text, DateTime expiresAt) => new()
    {
        Channel = StatusChannel.Transient,
        Variants = new[] { new[] { new StatusSegment(text, StatusStyle.Prompt) } },
        ExpiresAt = expiresAt,
    };

    private static StatusItem Ambient(string text, int priority = 0)
        => StatusItem.Text(StatusChannel.Ambient, StatusStyle.Secondary, text, priority);

    private static StatusItem Hints(params string[] tiers) => new()
    {
        Channel = StatusChannel.Hint,
        Variants = tiers.Select(t => new[] { new StatusSegment(t, StatusStyle.Dim) }).ToArray(),
    };

    private static StatusItem Help() => new()
    {
        Channel = StatusChannel.Hint,
        Variants = new[]
        {
            new[] { new StatusSegment("?", StatusStyle.Accent), new StatusSegment(":help", StatusStyle.Dim) },
            new[] { new StatusSegment("?", StatusStyle.Accent) },
        },
    };

    private static int PlainWidth(StatusLineModel model)
        => RenderHelpers.GetDisplayWidth(model.PlainText);

    // ---- Property: composed width never exceeds the budget ----

    [Fact]
    public void Compose_NeverExceedsBudget_AtAnyWidth()
    {
        var composer = new StatusComposer(new FakeTimeProvider(T0));

        for (var width = 40; width <= 200; width++)
        {
            var model = composer.Compose(
                width,
                left: new[] { Ambient("example.com") },
                right: new[]
                {
                    Alert("⏸ login at nytimes.com · Shift+O:open", "⏸ login·Shift+O"),
                    Transient("▶ Speed reading 350 WPM — <:slower >:faster f:stop", T0.AddSeconds(4)),
                    Ambient("⇉ docked"),
                    Ambient("3 sel"),
                },
                hints: Hints("Enter:open Space:select s:save ?:help", "s:save ?:help"),
                help: Help());

            PlainWidth(model).Should().BeLessThanOrEqualTo(width - 1,
                $"the composed line must fit the budget at width {width}");
        }
    }

    // ---- Property: alert visible at all widths ----

    [Fact]
    public void Compose_AlertIsVisible_AtEveryWidth()
    {
        var composer = new StatusComposer(new FakeTimeProvider(T0));

        for (var width = 40; width <= 200; width++)
        {
            var model = composer.Compose(
                width,
                left: new[] { Ambient("a-fairly-long-domain.example.com") },
                right: new[]
                {
                    Alert("⏸ login at nytimes.com · Shift+O:open", "⏸ login·Shift+O"),
                    Ambient("⇉ docked"),
                    Ambient("/search"),
                },
                hints: Hints("Enter:open Space:select s:save ?:help"),
                help: Help());

            model.PlainText.Should().Contain("⏸",
                $"the alert glyph must survive at width {width} — alerts are never dropped");
        }
    }

    [Fact]
    public void Compose_AlertVisibleAt50Cols_WithLongAndShortCopy()
    {
        var composer = new StatusComposer(new FakeTimeProvider(T0));

        var model = composer.Compose(
            50,
            left: new[] { Ambient("nytimes.com") },
            right: new[] { Alert("⏸ login at nytimes.com · Shift+O:open", "⏸ login·Shift+O") },
            help: Help());

        model.PlainText.Should().Contain("⏸ login",
            "the epic acceptance pins the HITL alert at 50-col width");
        PlainWidth(model).Should().BeLessThanOrEqualTo(49);
    }

    // ---- Property: degradation order is stable ----

    [Fact]
    public void Compose_DegradesAmbientBeforeTransient()
    {
        var composer = new StatusComposer(new FakeTimeProvider(T0));
        var ambient = new StatusItem
        {
            Channel = StatusChannel.Ambient,
            Variants = new[]
            {
                new[] { new StatusSegment("a-very-long-ambient-badge-text", StatusStyle.Secondary) },
                new[] { new StatusSegment("amb", StatusStyle.Secondary) },
            },
        };
        var transient = new StatusItem
        {
            Channel = StatusChannel.Transient,
            Variants = new[]
            {
                new[] { new StatusSegment("▶ Speed reading 350 WPM — <:slower >:faster", StatusStyle.Prompt) },
                new[] { new StatusSegment("▶350", StatusStyle.Prompt) },
            },
            ExpiresAt = T0.AddSeconds(4),
        };

        // Width chosen so exactly one of the two must degrade.
        var model = composer.Compose(
            80,
            left: Array.Empty<StatusItem>(),
            right: new[] { transient, ambient });

        model.PlainText.Should().Contain("▶ Speed reading 350 WPM",
            "the transient keeps its long copy while squeeze exists");
        model.PlainText.Should().Contain("amb",
            "the ambient badge absorbs the squeeze first by degrading to its short variant");
    }

    [Fact]
    public void Compose_DropsAmbientEntirely_BeforeTouchingTransient()
    {
        var composer = new StatusComposer(new FakeTimeProvider(T0));

        var model = composer.Compose(
            34,
            left: Array.Empty<StatusItem>(),
            right: new[]
            {
                Transient("✓ Saved (12) — c:list", T0.AddSeconds(3)),
                Ambient("⇉ docked", priority: 1),
                Ambient("/query", priority: 2),
                Ambient("3 sel", priority: 3),
            });

        model.PlainText.Should().Contain("✓ Saved (12)",
            "an active transient is never silently dropped while ambient badges remain");
        model.PlainText.Should().NotContain("3 sel", "the least important ambient badge drops first");
        model.PlainText.Should().NotContain("/query", "ambient badges keep dropping until the line fits");
        model.PlainText.Should().Contain("docked",
            "an ambient badge that still fits after the drops is kept — drops stop as soon as the line fits");
    }

    [Fact]
    public void Compose_DegradationOrderIsDeterministic()
    {
        // Same inputs, same width → byte-identical plain text, every time.
        var composer = new StatusComposer(new FakeTimeProvider(T0));
        StatusLineModel Compose() => composer.Compose(
            72,
            left: new[] { Ambient("example.com") },
            right: new[]
            {
                Alert("⏸ login at nytimes.com · Shift+O:open", "⏸ login·Shift+O"),
                Transient("Width 60 — [:narrow ]:widen", T0.AddSeconds(2)),
                Ambient("⇉ docked", priority: 1),
                Ambient("5 sel", priority: 2),
            },
            hints: Hints("Enter:open s:save ?:help", "?:help"),
            help: Help());

        var first = Compose().PlainText;
        for (var i = 0; i < 10; i++)
        {
            Compose().PlainText.Should().Be(first, "composition must be deterministic");
        }
    }

    // ---- Property: clock-based transient TTL ----

    [Fact]
    public void Compose_TransientSurvivesEveryRerender_UntilTtl()
    {
        var clock = new FakeTimeProvider(T0);
        var composer = new StatusComposer(clock);
        var transient = Transient("▶ Speed reading 350 WPM", T0.AddSeconds(4));

        // Many re-renders inside the TTL window — the render-count bug class
        // (MarkToastRendered killing toasts on the 2nd pass) must not exist here.
        for (var render = 0; render < 50; render++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(50));
            var model = composer.Compose(120, Array.Empty<StatusItem>(), new[] { transient });
            model.PlainText.Should().Contain("Speed reading",
                $"the transient must survive re-render #{render} ({(render + 1) * 50}ms < 4s TTL)");
        }

        clock.Set(T0.AddSeconds(4.5));
        var expired = composer.Compose(120, Array.Empty<StatusItem>(), new[] { transient });
        expired.PlainText.Should().NotContain("Speed reading", "the TTL has elapsed");
    }

    [Fact]
    public void Compose_StickyItemsNeverExpire()
    {
        var clock = new FakeTimeProvider(T0);
        var composer = new StatusComposer(clock);
        var alert = Alert("⏸ login at nytimes.com · Shift+O:open", "⏸ login·Shift+O");

        clock.Advance(TimeSpan.FromHours(2));
        var model = composer.Compose(120, Array.Empty<StatusItem>(), new[] { alert });

        model.PlainText.Should().Contain("⏸ login", "alerts are sticky until resolved, not TTL'd");
    }

    // ---- Hints absorb squeeze first ----

    [Fact]
    public void Compose_HintsOnlyRenderIntoLeftoverSpace()
    {
        var composer = new StatusComposer(new FakeTimeProvider(T0));

        var wide = composer.Compose(
            160,
            left: new[] { Ambient("example.com") },
            right: new[] { Ambient("✓ cached") },
            hints: Hints("Enter:open Space:select s:save A:save-all Shift+R:refresh v:reader ?:help", "s:save ?:help"));
        wide.Hints.Should().NotBeNull("a wide line has leftover space for the full hint tier");
        wide.PlainText.Should().Contain("save-all");

        var narrow = composer.Compose(
            48,
            left: new[] { Ambient("example.com") },
            right: new[] { Ambient("✓ cached") },
            hints: Hints("Enter:open Space:select s:save A:save-all Shift+R:refresh v:reader ?:help", "s:save ?:help"));
        narrow.PlainText.Should().NotContain("save-all", "the full tier cannot fit at 48 cols");
    }

    [Fact]
    public void Compose_HintsNeverDisplaceOtherChannels()
    {
        var composer = new StatusComposer(new FakeTimeProvider(T0));

        // With hints present, every other channel renders identically.
        var withHints = composer.Compose(
            70,
            left: new[] { Ambient("example.com") },
            right: new[]
            {
                Transient("✓ Saved (3) — c:list", T0.AddSeconds(3)),
                Ambient("⇉ docked"),
            },
            hints: Hints("Enter:open Space:select s:save ?:help", "?:help"));

        var withoutHints = composer.Compose(
            70,
            left: new[] { Ambient("example.com") },
            right: new[]
            {
                Transient("✓ Saved (3) — c:list", T0.AddSeconds(3)),
                Ambient("⇉ docked"),
            });

        withHints.Right.Count.Should().Be(withoutHints.Right.Count);
        withHints.PlainText.Should().Contain("✓ Saved (3)");
        withHints.PlainText.Should().Contain("docked");
    }

    // ---- Ordering ----

    [Fact]
    public void Compose_OrdersRightGroupByChannelThenPriority()
    {
        var composer = new StatusComposer(new FakeTimeProvider(T0));

        var model = composer.Compose(
            200,
            left: Array.Empty<StatusItem>(),
            right: new[]
            {
                Ambient("zz-ambient", priority: 5),
                Transient("transient-text", T0.AddSeconds(3)),
                Ambient("aa-ambient", priority: 1),
                Alert("alert-text", "alert"),
            });

        var plain = model.PlainText;
        var alertIdx = plain.IndexOf("alert-text", StringComparison.Ordinal);
        var transientIdx = plain.IndexOf("transient-text", StringComparison.Ordinal);
        var aaIdx = plain.IndexOf("aa-ambient", StringComparison.Ordinal);
        var zzIdx = plain.IndexOf("zz-ambient", StringComparison.Ordinal);

        alertIdx.Should().BeLessThan(transientIdx, "Alert renders before Transient");
        transientIdx.Should().BeLessThan(aaIdx, "Transient renders before Ambient");
        aaIdx.Should().BeLessThan(zzIdx, "within a channel, lower priority value renders first");
    }

    // ---- Painter ----

    [Fact]
    public void FormatStatusLine_PaintsSegmentsWithThemeTokens()
    {
        var p = BuiltInThemes.Get(WireCopy.Domain.Enums.Browser.ThemeName.Phosphor);
        var composer = new StatusComposer(new FakeTimeProvider(T0));
        var model = composer.Compose(
            100,
            left: new[] { Ambient("example.com") },
            right: new[] { Alert("⏸ login at nytimes.com · Shift+O:open", "⏸ login·Shift+O") },
            help: Help());

        var line = StatusBarRenderer.FormatStatusLine(model, p);

        line.Should().Contain(p.GetWarningFg().AnsiFg, "alert segments paint with the warning token");
        line.Should().Contain(p.GetAccentFg().AnsiFg, "the help key paints with the accent token");
        line.Should().Contain("\x1b[0m", "every run resets");
    }
}
