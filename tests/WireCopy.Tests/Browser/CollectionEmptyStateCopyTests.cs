// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using NSubstitute;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Entities.Collections;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Infrastructure.Browser.UI.Renderers;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// workspace-wyxx.2: the empty-collection message must name the prerequisite
/// AND the way back to it — the user is inside the collection view, so the
/// missing step is "go back to your articles", not just the `s` key.
/// </summary>
[Trait("Category", "Unit")]
[Collection(WireCopy.Tests.ConsoleSerialCollection.Name)]
public class CollectionEmptyStateCopyTests
{
    [Fact]
    public void RenderCollectionItems_EmptyCollection_GuidesBackToArticles()
    {
        var stripped = StripAnsi(RenderEmptyCollection());

        stripped.Should().Contain("Nothing saved yet");
        stripped.Should().Contain("go back to your articles",
            "the message must name the way back, not just the save key");
        stripped.Should().Contain("press");
        stripped.Should().Contain("s");
        stripped.Should().Contain("to add them here");
    }

    [Fact]
    public void RenderCollectionItems_NonEmptyCollection_OmitsEmptyStateCopy()
    {
        var collection = Collection.Create("Reading List");
        collection.AddItem("https://example.com/a", "An Article");

        var stripped = StripAnsi(RenderCollection(collection));

        stripped.Should().NotContain("Nothing saved yet");
        stripped.Should().Contain("An Article");
    }

    private static string RenderEmptyCollection()
    {
        return RenderCollection(Collection.Create("Reading List"));
    }

    private static string RenderCollection(Collection collection)
    {
        var themeProvider = Substitute.For<IThemeProvider>();
        themeProvider.CurrentTheme.Returns(ThemeName.Phosphor);

        var helpers = new RenderHelpers { TerminalHeight = 30 };
        var renderer = new CollectionRenderer(helpers, themeProvider);

        var options = new RenderOptions
        {
            TerminalWidth = 100,
            TerminalHeight = 30,
        };

        var originalOut = Console.Out;
        using var sw = new StringWriter();
        Console.SetOut(sw);
        try
        {
            renderer.RenderCollectionItems(collection, selectedIndex: 0, scrollOffset: 0, options);
            return sw.ToString();
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    private static string StripAnsi(string text)
    {
        return System.Text.RegularExpressions.Regex.Replace(
            text, @"\x1b\[[0-9;]*[A-Za-z]", string.Empty);
    }
}
