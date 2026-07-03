// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Browser.UI.Renderers;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// workspace-6z3a.2 — the status bar's search indicator shows WHERE the user is
/// in the results ("/query 2/14 (n/N)") and is explicit when the search found
/// nothing ("/query 0 matches") instead of rendering a bare query.
/// </summary>
[Trait("Category", "Unit")]
public class StatusBarSearchIndicatorTests
{
    private static NavigationContext Context(string? query, int matchIndex = 0, int matchCount = 0) => new()
    {
        ViewMode = ViewMode.Hierarchical,
        SearchQuery = query,
        SearchMatchIndex = matchIndex,
        SearchMatchCount = matchCount,
    };

    [Fact]
    public void ActiveSearch_WithMatches_ShowsPositionAndCount()
    {
        var model = StatusBarRenderer.ComposeStatusLine(
            Context("robot", matchIndex: 1, matchCount: 14), ViewMode.Hierarchical, 160);

        model.PlainText.Should().Contain("/robot 2/14 (n/N)");
    }

    [Fact]
    public void ActiveSearch_FirstMatch_ShowsOneBasedPosition()
    {
        var model = StatusBarRenderer.ComposeStatusLine(
            Context("robot", matchIndex: 0, matchCount: 3), ViewMode.Hierarchical, 160);

        model.PlainText.Should().Contain("/robot 1/3 (n/N)");
    }

    [Fact]
    public void ActiveSearch_ZeroMatches_SaysSoExplicitly()
    {
        var model = StatusBarRenderer.ComposeStatusLine(
            Context("zebra", matchCount: 0), ViewMode.Hierarchical, 160);

        model.PlainText.Should().Contain("/zebra 0 matches");
        model.PlainText.Should().NotContain("(n/N)", "n/N is pointless with nothing to jump to");
    }

    [Fact]
    public void NoActiveSearch_RendersNoSearchIndicator()
    {
        var model = StatusBarRenderer.ComposeStatusLine(
            Context(query: null), ViewMode.Hierarchical, 160);

        model.PlainText.Should().NotContain("matches");
        model.PlainText.Should().NotContain("(n/N)");
    }
}
