// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Infrastructure.Browser.CommandHandlers;
using WireCopy.Infrastructure.Browser.Themes;
using WireCopy.Infrastructure.Browser.UI.Renderers;
using Xunit;

namespace WireCopy.Tests.Podcast;

/// <summary>
/// workspace-nahg item 3: the progress screen advertises 'd' as the explicit
/// detach verb (alongside Esc:back and x:cancel) so backgrounding a run is a
/// discoverable action rather than an overloaded Esc.
/// </summary>
[Trait("Category", "Unit")]
[Collection(WireCopy.Tests.ConsoleSerialCollection.Name)]
public class ProgressScreenDetachHintTests
{
    private static readonly ThemePalette Palette =
        BuiltInThemes.Get(WireCopy.Domain.Enums.Browser.ThemeName.Phosphor);

    [Fact]
    public void ProgressScreen_HintLine_AdvertisesDetachEscAndCancel()
    {
        var statuses = new[]
        {
            new PodcastCommandHandler.ArticleStatus
            {
                Title = "Some article",
                State = PodcastCommandHandler.ArticleState.Pending,
            },
        };

        var output = ConsoleCapture.Render(h =>
            PodcastProgressScreens.RenderProgressContent(
                h, Palette, progress: null, animFrame: 0, statuses, terminalWidth: 100, terminalHeight: 35));

        output.Should().Contain("d");
        output.Should().Contain(":detach (keeps generating)",
            "detach must be a named, discoverable verb on the progress screen");
        output.Should().Contain(":back",
            "Esc keeps working as the safe exit");
        output.Should().Contain(":cancel run",
            "'x' stays the deliberate cancel keystroke");
    }

}
