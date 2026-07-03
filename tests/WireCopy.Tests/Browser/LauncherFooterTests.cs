// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using NSubstitute;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Infrastructure.Browser.UI.Renderers;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// workspace-ej1i: launcher footer behaviour. `?` is labelled "all keys" (the
/// footer only shows five of the launcher's bindings), and the auxiliary
/// footer line paints the active transient status message — pre-fix the
/// launcher swallowed every SetStatusMessage (delete/reorder feedback was set
/// but never rendered anywhere on the launcher screen).
/// </summary>
[Trait("Category", "Unit")]
[Collection(WireCopy.Tests.ConsoleSerialCollection.Name)]
public class LauncherFooterTests
{
    [Fact]
    public void RenderFooter_LabelsHelpKeyAsAllKeys()
    {
        var (output, _) = CaptureFooter();
        var stripped = StripAnsi(output);

        stripped.Should().Contain("[?] all keys",
            "workspace-ej1i.3: the footer must advertise that ? reveals the full key list");
        stripped.Should().NotContain("[?] help",
            "the vague 'help' label hid that more keys exist beyond the footer");
    }

    [Fact]
    public void RenderFooter_PaintsStatusMessage()
    {
        var (output, linesWritten) = CaptureFooter(statusMessage: "Already at top");

        StripAnsi(output).Should().Contain("Already at top",
            "launcher status feedback must be visible — SetStatusMessage was previously swallowed on this screen");
        linesWritten.Should().Be(2, "status message occupies the auxiliary line above the hints");
    }

    [Fact]
    public void RenderFooter_NoStatusMessage_OmitsAuxiliaryLine()
    {
        var (_, linesWritten) = CaptureFooter();

        linesWritten.Should().Be(1,
            "without a badge or status message the footer is the single hint line");
    }

    [Fact]
    public void RenderFooter_ScheduledRunBadge_TakesPriorityOverStatusMessage()
    {
        var (output, linesWritten) = CaptureFooter(
            scheduledRunBadge: "1 scheduled run failed",
            statusMessage: "Already at top");
        var stripped = StripAnsi(output);

        stripped.Should().Contain("1 scheduled run failed",
            "failure badges are the higher-stakes signal for the auxiliary line");
        stripped.Should().NotContain("Already at top",
            "only one auxiliary line exists; the badge wins the slot");
        linesWritten.Should().Be(2);
    }

    private static (string Output, int LinesWritten) CaptureFooter(
        string? scheduledRunBadge = null, string? statusMessage = null)
    {
        var themeProvider = Substitute.For<IThemeProvider>();
        themeProvider.CurrentTheme.Returns(ThemeName.Phosphor);

        var helpers = new RenderHelpers { TerminalHeight = 24 };
        var renderer = new LauncherRenderer(helpers, themeProvider);

        var originalOut = Console.Out;
        using var sw = new StringWriter();
        Console.SetOut(sw);
        try
        {
            renderer.RenderFooter(80, scheduledRunBadge, statusMessage);
            return (sw.ToString(), helpers.LinesWritten);
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
