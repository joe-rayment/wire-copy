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
public class LinkTreeRendererBurstTests
{
    private static readonly Regex SelectionBgEscape = new(@"\x1b\[48;5;\d+m", RegexOptions.Compiled);
    private const string AccentBar = "▌"; // ▌

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
