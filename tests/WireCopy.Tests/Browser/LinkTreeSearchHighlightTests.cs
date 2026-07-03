// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Domain.Entities.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Browser.Themes;
using WireCopy.Infrastructure.Browser.UI.Renderers;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// workspace-6z3a.5 — the hierarchical (link tree) view marks WHY a row matched
/// the active search: the matching substring inside the card title renders in
/// the theme's search-highlight colors, and the surrounding color state (e.g.
/// the selected-row background) is restored right after the match.
/// </summary>
[Trait("Category", "Unit")]
public class LinkTreeSearchHighlightTests
{
    private const string Reset = "\x1b[0m";
    private const string Bold = "\x1b[1m";
    private static readonly ThemePalette TestPalette = BuiltInThemes.Get(ThemeName.Phosphor);

    private static string HighlightStart =>
        $"{TestPalette.SearchHighlightBg.AnsiBg}{TestPalette.SearchHighlightFg.AnsiFg}";

    private static LinkNode CreateLinkNode(string displayText)
    {
        var root = LinkNode.CreateRoot();
        var link = new LinkInfo
        {
            DisplayText = displayText,
            Url = "https://example.com/story",
            Type = LinkType.Content,
            ImportanceScore = 50,
        };
        return root.AddChild(link);
    }

    // ---- HighlightSegment ----

    [Fact]
    public void HighlightSegment_WrapsMatch_AndRestoresSurroundingState()
    {
        var result = LinkTreeRenderer.HighlightSegment("OpenAI ships robots", "robot", TestPalette, "RESUME");

        result.Should().Be($"OpenAI ships {HighlightStart}robot{Reset}RESUMEs");
    }

    [Fact]
    public void HighlightSegment_MatchesCaseInsensitively()
    {
        var result = LinkTreeRenderer.HighlightSegment("Robot news", "robot", TestPalette, "R");

        result.Should().Be($"{HighlightStart}Robot{Reset}R news");
    }

    [Fact]
    public void HighlightSegment_HighlightsEveryOccurrence()
    {
        var result = LinkTreeRenderer.HighlightSegment("ai beats ai", "ai", TestPalette, "R");

        result.Should().Be($"{HighlightStart}ai{Reset}R beats {HighlightStart}ai{Reset}R");
    }

    [Fact]
    public void HighlightSegment_NoMatch_ReturnsTextUnchanged()
    {
        LinkTreeRenderer.HighlightSegment("OpenAI ships robots", "zebra", TestPalette, "R")
            .Should().Be("OpenAI ships robots");
    }

    [Fact]
    public void HighlightSegment_NullOrEmptyQuery_ReturnsTextUnchanged()
    {
        LinkTreeRenderer.HighlightSegment("OpenAI ships robots", null, TestPalette, "R")
            .Should().Be("OpenAI ships robots");
        LinkTreeRenderer.HighlightSegment("OpenAI ships robots", string.Empty, TestPalette, "R")
            .Should().Be("OpenAI ships robots");
    }

    // ---- BuildCardLine wiring (the actual link-tree render path) ----

    [Fact]
    public void BuildCardLine_NormalTitle_HighlightsMatchingSubstring()
    {
        var node = CreateLinkNode("OpenAI ships robots");

        var line = LinkTreeRenderer.BuildCardLine(
            node, isSelected: false, cardHeight: 2, lineIndex: 0, width: 60,
            TestPalette, cachedUrls: null, isToggled: false, searchQuery: "robot");

        line.Should().Contain($"{HighlightStart}robot{Reset}");
    }

    [Fact]
    public void BuildCardLine_SelectedTitle_HighlightsMatch_AndRestoresSelectionBackground()
    {
        var node = CreateLinkNode("OpenAI ships robots");

        var line = LinkTreeRenderer.BuildCardLine(
            node, isSelected: true, cardHeight: 2, lineIndex: 0, width: 60,
            TestPalette, cachedUrls: null, isToggled: false, searchQuery: "robot");

        var selectionResume = $"{TestPalette.SelectedItemBg.AnsiBg}{TestPalette.SelectedItemFg.AnsiFg}{Bold}";
        line.Should().Contain(
            $"{HighlightStart}robot{Reset}{selectionResume}",
            "the selected-row background must survive past the highlighted match");
    }

    [Fact]
    public void BuildCardLine_WithoutQuery_ContainsNoHighlightCodes()
    {
        var node = CreateLinkNode("OpenAI ships robots");

        var line = LinkTreeRenderer.BuildCardLine(
            node, isSelected: false, cardHeight: 2, lineIndex: 0, width: 60, TestPalette);

        line.Should().NotContain(TestPalette.SearchHighlightBg.AnsiBg);
    }

    [Fact]
    public void BuildCardLine_QueryNotInTitle_LeavesLineUnchanged()
    {
        var node = CreateLinkNode("OpenAI ships robots");

        var plain = LinkTreeRenderer.BuildCardLine(
            node, isSelected: false, cardHeight: 2, lineIndex: 0, width: 60, TestPalette);
        var searched = LinkTreeRenderer.BuildCardLine(
            node, isSelected: false, cardHeight: 2, lineIndex: 0, width: 60,
            TestPalette, cachedUrls: null, isToggled: false, searchQuery: "zebra");

        searched.Should().Be(plain);
    }
}
