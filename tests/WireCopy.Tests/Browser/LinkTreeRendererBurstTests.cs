// Licensed under the MIT License. See LICENSE in the repository root.

using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using NSubstitute;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Entities.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Browser.UI.Renderers;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// Regression coverage for workspace-1f5a: under sustained rapid input the cursor
/// highlight escape sequence drops out of the rendered output. These tests render
/// the link tree N times in succession with the selection advancing each frame
/// and assert every captured frame includes a cursor highlight indicator.
///
/// If these tests pass it means the renderer's output construction is deterministic
/// and the bug is NOT in the SGR composition — it's somewhere downstream (Console.Out
/// buffering, terminal/tmux timing, or input race in BrowserOrchestrator). That is a
/// useful negative result and the bead's investigation notes call for it.
/// </summary>
[Trait("Category", "Unit")]
[Collection(WireCopy.Tests.ConsoleSerialCollection.Name)]
public class LinkTreeRendererBurstTests
{
    private static readonly Regex SelectionBgEscape = new(@"\x1b\[48;5;\d+m", RegexOptions.Compiled);
    private const string AccentBar = "▌"; // ▌

    /// <summary>
    /// Render-level width guard for the responsive story-list grid (workspace-ehon). Invokes the
    /// REAL RenderGridRow at a NONZERO-remainder 3-column width, so a regression where the last
    /// column used layout.CellWidth instead of ResponsiveGrid.LastCellWidthFor shows up as a short
    /// separator rule (a ragged / clipped right edge). The composed reconstruction tests can't catch
    /// this — they re-do the last-cell arithmetic themselves — and the earlier width test split
    /// evenly (remainder 0), so the renderer's width-selection was never exercised (review finding).
    /// </summary>
    [Fact]
    public void RenderLinkTree_ThreeColumns_NonzeroRemainder_SeparatorRowsFillFullWidth()
    {
        const int width = 170; // inner 168 → 3 cols, base cell 55, remainder-absorbing last cell 56
        var layout = LinkTreeRenderer.ComputeLayout(width, 40);
        layout.Columns.Should().Be(3);

        var linkMap = new Dictionary<LinkType, List<LinkInfo>>
        {
            [LinkType.Content] = Enumerable.Range(0, 6).Select(i => new LinkInfo
            {
                Url = $"https://example.com/a-{i}",
                DisplayText = $"Article {i} headline",
                Type = LinkType.Content,
                ImportanceScore = 70,
            }).ToList(),
        };
        var tree = NavigationTree.BuildWithGroups(linkMap);
        tree.EnsureSelection();

        var themeProvider = Substitute.For<IThemeProvider>();
        themeProvider.CurrentTheme.Returns(ThemeName.Phosphor);
        var helpers = new RenderHelpers { TerminalHeight = 40 };
        var renderer = new LinkTreeRenderer(helpers, themeProvider);
        var options = new RenderOptions
        {
            TerminalWidth = width,
            TerminalHeight = 40,
            MaxContentWidth = width - 4,
            LayoutVariant = "Cards",
        };
        var context = new NavigationContext { ScrollOffset = 0 };

        var originalOut = Console.Out;
        string captured;
        try
        {
            using var sw = new StringWriter();
            Console.SetOut(sw);
            renderer.RenderLinkTree(tree, context, maxLines: 35, options);
            captured = sw.ToString();
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        // The TUI positions lines with cursor escapes (no newlines); each WriteLine emits a
        // leading clear-line \x1b[K, so split on that to recover individual lines, then strip
        // the colour/cursor escapes to measure the visible width.
        var ruleRows = 0;
        foreach (var seg in captured.Split(new[] { "\x1b[K" }, StringSplitOptions.None))
        {
            var text = Regex.Replace(seg, @"\x1b\[[0-9;]*[A-Za-z]", string.Empty);
            if (!text.Contains('┼'))
            {
                continue;
            }

            ruleRows++;
            text.Length.Should().Be(layout.Width,
                "every 3-column separator row must fill the content width (last column absorbs the remainder)");
        }

        ruleRows.Should().BeGreaterThan(0, "the 3-column story list must render ┼ separator rows");
    }

    /// <summary>
    /// workspace-1f5a: simulates the user's reported scenario — 25 rapid 'j'
    /// keystrokes advancing the selection one row per frame — and asserts the
    /// cursor highlight escape is present in EVERY captured frame across all
    /// four built-in themes. With frame buffering enabled at the orchestrator
    /// level, the cursor escape is composed atomically per frame so it cannot
    /// be split mid-write by an OS pipe-buffer flush. This test asserts the
    /// frame composition is deterministic; the orchestrator-level wrapping
    /// guarantees the atomic emit.
    /// </summary>
    [Theory]
    [InlineData(ThemeName.Phosphor)]
    [InlineData(ThemeName.Amber)]
    [InlineData(ThemeName.Dracula)]
    [InlineData(ThemeName.Light)]
    public void RenderLinkTree_RapidJBurst_AllThemesAlwaysContainCursorHighlight(ThemeName theme)
    {
        const int totalLinks = 30;
        const int iterations = 25;
        var linkMap = new Dictionary<LinkType, List<LinkInfo>>
        {
            [LinkType.Content] = Enumerable.Range(0, totalLinks)
                .Select(i => new LinkInfo
                {
                    Url = $"https://example.com/article-{i}",
                    DisplayText = $"Article {i} headline goes here",
                    Type = LinkType.Content,
                    ImportanceScore = 70,
                })
                .ToList(),
        };
        var tree = NavigationTree.BuildWithGroups(linkMap);

        var themeProvider = Substitute.For<IThemeProvider>();
        themeProvider.CurrentTheme.Returns(theme);

        var capturedFrames = new List<string>();
        var originalOut = Console.Out;
        try
        {
            tree.EnsureSelection();
            var visibleNodes = tree.GetVisibleNodes().Where(n => !n.IsGroupHeader).ToList();
            var cycleCount = Math.Min(10, visibleNodes.Count);

            for (var frame = 0; frame < iterations; frame++)
            {
                if (cycleCount > 0)
                {
                    var targetIdx = frame % cycleCount;
                    tree.SelectNodeById(visibleNodes[targetIdx].Id);
                }

                tree.EnsureSelection();

                // Use frame buffering so the captured Console.Out reflects the
                // single-write path the orchestrator uses in production.
                var helpers = new RenderHelpers { TerminalHeight = 40 };
                helpers.BeginFrame();
                var renderer = new LinkTreeRenderer(helpers, themeProvider);
                var options = new RenderOptions
                {
                    TerminalWidth = 120,
                    TerminalHeight = 40,
                    MaxContentWidth = 116,
                    LayoutVariant = "Cards",
                };
                var context = new NavigationContext { ScrollOffset = 0 };

                var sw = new StringWriter();
                Console.SetOut(sw);
                renderer.RenderLinkTree(tree, context, maxLines: 35, options);
                helpers.EndFrame();
                Console.Out.Flush();

                capturedFrames.Add(sw.ToString());
            }
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        capturedFrames.Should().HaveCount(iterations);

        var missing = new List<int>();
        for (var i = 0; i < capturedFrames.Count; i++)
        {
            var f = capturedFrames[i];
            var hasBar = f.Contains(AccentBar, StringComparison.Ordinal);
            var hasSelBg = SelectionBgEscape.IsMatch(f);
            var hasUnderlineColor = f.Contains("\x1b[58;5;", StringComparison.Ordinal);
            if (!hasBar && !hasSelBg && !hasUnderlineColor)
            {
                missing.Add(i);
            }
        }

        if (missing.Count > 0)
        {
            var firstMissing = missing[0];
            var debug = new StringBuilder()
                .AppendLine($"theme={theme} missing={missing.Count}/{capturedFrames.Count}")
                .AppendLine($"first missing frame {firstMissing}:")
                .AppendLine(capturedFrames[firstMissing].Replace("\x1b", "\\e", StringComparison.Ordinal))
                .ToString();
            missing.Should().BeEmpty(debug);
        }
    }

    [Theory]
    [InlineData("Cards")]
    public void RenderLinkTree_SustainedSelectionAdvance_AlwaysEmitsCursorHighlight(string layoutVariant)
    {
        const int totalLinks = 40;
        var linkMap = new Dictionary<LinkType, List<LinkInfo>>
        {
            [LinkType.Content] = Enumerable.Range(0, totalLinks)
                .Select(i => new LinkInfo
                {
                    Url = $"https://example.com/article-{i}",
                    DisplayText = $"Article {i} headline goes here",
                    Type = LinkType.Content,
                    ImportanceScore = 70,
                })
                .ToList(),
        };
        var tree = NavigationTree.BuildWithGroups(linkMap);

        var themeProvider = Substitute.For<IThemeProvider>();
        themeProvider.CurrentTheme.Returns(ThemeName.Phosphor);

        var capturedFrames = new List<string>();
        var originalOut = Console.Out;
        try
        {
            tree.EnsureSelection();
            var visibleNodes = tree.GetVisibleNodes().Where(n => !n.IsGroupHeader).ToList();

            // Limit selection cycling to nodes that fit in the visible viewport.
            // Cards layout with 35 maxLines and cellHeight=5 fits ~7 rows × 2 cols = 14 cards;
            // 10 is a safe lower bound that exercises the burst pattern without sliding
            // selection offscreen.
            var visibleCount = Math.Min(10, visibleNodes.Count);

            for (var frame = 0; frame < 20; frame++)
            {
                if (visibleCount > 0)
                {
                    var targetIdx = frame % visibleCount;
                    tree.SelectNodeById(visibleNodes[targetIdx].Id);
                }

                tree.EnsureSelection();

                var helpers = new RenderHelpers { TerminalHeight = 40 };
                var renderer = new LinkTreeRenderer(helpers, themeProvider);
                var options = new RenderOptions
                {
                    TerminalWidth = 120,
                    TerminalHeight = 40,
                    MaxContentWidth = 116,
                    LayoutVariant = layoutVariant,
                };
                var context = new NavigationContext { ScrollOffset = 0 };

                var sw = new StringWriter();
                Console.SetOut(sw);
                renderer.RenderLinkTree(tree, context, maxLines: 35, options);
                Console.Out.Flush();

                capturedFrames.Add(sw.ToString());
            }
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        var missing = new List<int>();
        for (var i = 0; i < capturedFrames.Count; i++)
        {
            var f = capturedFrames[i];
            var hasBar = f.Contains(AccentBar, StringComparison.Ordinal);
            var hasSelBg = SelectionBgEscape.IsMatch(f);
            if (!hasBar && !hasSelBg)
            {
                missing.Add(i);
            }
        }

        if (missing.Count > 0)
        {
            var firstMissing = missing[0];
            var debug = new StringBuilder()
                .AppendLine($"layout={layoutVariant} missing={missing.Count}/{capturedFrames.Count}")
                .AppendLine($"first missing frame {firstMissing}:")
                .AppendLine(capturedFrames[firstMissing].Replace("\x1b", "\\e", StringComparison.Ordinal))
                .ToString();
            missing.Should().BeEmpty(debug);
        }
    }

}
