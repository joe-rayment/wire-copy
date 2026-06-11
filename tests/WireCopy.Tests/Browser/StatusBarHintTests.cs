// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Domain.Entities.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Browser.UI.Components;
using WireCopy.Infrastructure.Browser.UI.Renderers;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// workspace-wef6.3 — the adaptive hint system is live again: GetAdaptiveHints
/// tiers render into the status line's leftover space, tiers are
/// state-sensitive (speed-read / selection / prefetch panel override the
/// generic per-view hints), and no hint ever advertises a key the keybinding
/// popup doesn't know about.
/// </summary>
[Trait("Category", "Unit")]
public class StatusBarHintTests
{
    private static NavigationContext Context(
        ViewMode mode,
        bool speedRead = false,
        int selectionCount = 0)
    {
        Page? page = null;
        if (selectionCount > 0)
        {
            page = Page.Create(
                "https://example.com",
                "<html></html>",
                new PageMetadata { Title = "Test" });
            var links = Enumerable.Range(1, selectionCount)
                .Select(i => new LinkInfo
                {
                    DisplayText = $"Link {i}",
                    Url = $"https://example.com/{i}",
                    Type = LinkType.Content,
                    ImportanceScore = 1,
                })
                .ToList();
            var tree = NavigationTree.Build(links);
            foreach (var node in tree.GetAllNodes().Take(selectionCount))
            {
                tree.SelectedNodeIds.Add(node.Id);
            }

            page.SetLinkTree(tree);
        }

        return new NavigationContext
        {
            ViewMode = mode,
            CurrentPage = page,
            IsSpeedReadActive = speedRead,
            SpeedReadWpm = 350,
        };
    }

    // ---- Hints render into the leftover space (the dead code is live) ----

    [Fact]
    public void Hierarchical_WideLine_ShowsGenericHintTier()
    {
        var model = StatusBarRenderer.ComposeStatusLine(Context(ViewMode.Hierarchical), ViewMode.Hierarchical, 160);

        model.Hints.Should().NotBeNull("GetAdaptiveHints was dead code — wef6.3 wires it into leftover space");
        model.PlainText.Should().Contain("Enter:open");
        model.PlainText.Should().Contain("s:save");
    }

    [Fact]
    public void Readable_WideLine_ShowsReaderHintTier()
    {
        var model = StatusBarRenderer.ComposeStatusLine(Context(ViewMode.Readable), ViewMode.Readable, 160);

        model.PlainText.Should().Contain("f:speed-read");
        model.PlainText.Should().Contain("v:links");
    }

    [Fact]
    public void NarrowLine_DegradesToSmallerTier_NeverOverflows()
    {
        for (var width = 40; width <= 160; width += 5)
        {
            var model = StatusBarRenderer.ComposeStatusLine(Context(ViewMode.Hierarchical), ViewMode.Hierarchical, width);
            RenderHelpers.GetDisplayWidth(model.PlainText).Should().BeLessThanOrEqualTo(width - 1,
                $"hints must absorb the squeeze at width {width}, never overflow");
        }
    }

    // ---- State-sensitive overrides ----

    [Fact]
    public void SpeedReadActive_HintsTeachSpeedControls()
    {
        var model = StatusBarRenderer.ComposeStatusLine(
            Context(ViewMode.Readable, speedRead: true), ViewMode.Readable, 160);

        model.PlainText.Should().Contain("</>:speed", "active speed-read teaches its own controls");
        model.PlainText.Should().Contain("f:stop");
        model.PlainText.Should().NotContain("f:speed-read", "the generic reader tier is replaced while speed-reading");
    }

    [Fact]
    public void SelectionActive_HintsTeachSelectionKeys()
    {
        var model = StatusBarRenderer.ComposeStatusLine(
            Context(ViewMode.Hierarchical, selectionCount: 2), ViewMode.Hierarchical, 160);

        model.PlainText.Should().Contain("s:save-sel", "a non-empty selection teaches the save-selection flow");
        model.PlainText.Should().Contain("Esc:clear");
    }

    [Fact]
    public void PreloadPanelOpen_HintsTeachClose()
    {
        var model = StatusBarRenderer.ComposeStatusLine(
            Context(ViewMode.Hierarchical), ViewMode.Hierarchical, 160, preloadDetailVisible: true);

        model.PlainText.Should().Contain("\\:close", "an open prefetch panel teaches how to close it");
        model.PlainText.Should().NotContain("Enter:open", "the generic tier is replaced while the panel is open");
    }

    [Fact]
    public void PreviewMode_KeepsCarouselControls_NoGenericHints()
    {
        var context = new NavigationContext
        {
            ViewMode = ViewMode.Hierarchical,
            IsInPreviewMode = true,
            PreviewLabel = "Preview 1/3",
        };

        var model = StatusBarRenderer.ComposeStatusLine(context, ViewMode.Hierarchical, 160);

        model.Hints.Should().BeNull("preview mode keeps its dedicated carousel help instead of generic hints");
        model.PlainText.Should().Contain(":cycle");
        model.PlainText.Should().Contain(":save");
        model.PlainText.Should().Contain(":cancel");
    }

    // ---- Hints never advertise a dead key ----

    [Theory]
    [InlineData(ViewMode.Hierarchical)]
    [InlineData(ViewMode.Readable)]
    [InlineData(ViewMode.CollectionList)]
    [InlineData(ViewMode.CollectionItems)]
    public void EveryHintKey_ExistsInKeybindingPopup(ViewMode mode)
    {
        // Single source of truth check: the hint slot and the ? popup must
        // agree. A hint advertising a key the popup doesn't list is either a
        // dead key or an undocumented one — both are bugs.
        var popupKeys = KeybindingPopup.GetBindings(mode)
            .SelectMany(b => b.Key.Split('/', StringSplitOptions.TrimEntries))
            .Select(NormalizeKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // The popup omits a few universal keys that hints may teach.
        popupKeys.Add("?");
        popupKeys.Add("esc");
        popupKeys.Add(":");

        var contexts = new[]
        {
            Context(mode),
            Context(mode, speedRead: mode == ViewMode.Readable),
            Context(mode, selectionCount: mode == ViewMode.Hierarchical ? 2 : 0),
        };

        foreach (var context in contexts)
        {
            var model = StatusBarRenderer.ComposeStatusLine(context, mode, 220);
            if (model.Hints == null)
            {
                continue;
            }

            var hintKeys = model.Hints
                .Where(s => s.Style == WireCopy.Infrastructure.Browser.UI.StatusLine.StatusStyle.Accent)
                .SelectMany(s => s.Text.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Select(NormalizeKey)
                .Where(k => k.Length > 0);

            foreach (var key in hintKeys)
            {
                popupKeys.Should().Contain(key,
                    $"hint key '{key}' in {mode} must exist in the keybinding popup — hints must never advertise a dead key");
            }
        }
    }

    private static string NormalizeKey(string key)
    {
        var k = key.Trim();
        if (k.StartsWith("Shift+", StringComparison.OrdinalIgnoreCase))
        {
            k = k["Shift+".Length..];
        }

        // "[]" hint form ↔ "[ / ]" popup form; "<>"/"</>" ↔ "< / >".
        k = k.Replace("[]", "[").Replace("<>", "<");
        return k;
    }
}
