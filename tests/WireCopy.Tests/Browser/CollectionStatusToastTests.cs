// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Entities.Collections;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Infrastructure.Browser.UI;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// workspace-zzjy: collection views previously synthesized an EMPTY
/// NavigationContext for their status bar, silently dropping every transient
/// toast (most visibly "Cancelled — N articles completed" after an x-cancel).
/// The live status message now travels via RenderOptions.StatusMessage.
/// </summary>
[Trait("Category", "Unit")]
[Collection(WireCopy.Tests.ConsoleSerialCollection.Name)]
public class CollectionStatusToastTests
{
    private static TerminalPageRenderer CreateRenderer()
    {
        var themeProvider = Substitute.For<IThemeProvider>();
        themeProvider.CurrentTheme.Returns(ThemeName.Phosphor);
        return new TerminalPageRenderer(themeProvider, Substitute.For<ILogger<TerminalPageRenderer>>());
    }

    private static string Capture(Action action)
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

    private static RenderOptions Options(string? statusMessage) => new()
    {
        TerminalWidth = 120,
        TerminalHeight = 40,
        MaxContentWidth = 120,
        StatusMessage = statusMessage,
    };

    [Fact]
    public void RenderCollectionItems_ShowsLiveStatusToast()
    {
        var collection = Collection.Create("Reading List");
        collection.AddItem("https://example.com/a", "A Story");

        var output = Capture(() => CreateRenderer().RenderCollectionItems(
            collection, selectedIndex: 0, scrollOffset: 0,
            Options("Cancelled — 2 articles completed")));

        output.Should().Contain("Cancelled — 2 articles completed",
            "the collection view must not swallow transient status toasts");
    }

    [Fact]
    public void RenderCollectionList_ShowsLiveStatusToast()
    {
        var collections = new List<Collection> { Collection.Create("Reading List") };

        var output = Capture(() => CreateRenderer().RenderCollectionList(
            collections, selectedIndex: 0, defaultCollectionId: null, scrollOffset: 0,
            Options("✔ Saved")));

        output.Should().Contain("✔ Saved");
    }

    [Fact]
    public void RenderCollectionItems_NoToast_RendersNormally()
    {
        var collection = Collection.Create("Reading List");

        var output = Capture(() => CreateRenderer().RenderCollectionItems(
            collection, selectedIndex: 0, scrollOffset: 0, Options(null)));

        output.Should().Contain("Reading List");
    }
}
