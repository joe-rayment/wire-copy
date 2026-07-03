// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using WireCopy.Application.Interfaces;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Infrastructure.Browser.UI;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// workspace-mokw item 1: the manual-login wait screen carries a spinner and
/// an "elapsed (timeout)" clock so the 3-minute poll loop doesn't read as a
/// hang. Pins <see cref="TerminalPageRenderer.FormatWaitClock"/> and the
/// rendered waiting line.
/// </summary>
[Trait("Category", "Unit")]
[Collection(WireCopy.Tests.ConsoleSerialCollection.Name)]
public class ManualLoginWaitRenderTests
{
    [Theory]
    [InlineData(0, "0s")]
    [InlineData(999, "0s")]
    [InlineData(45_000, "45s")]
    [InlineData(60_000, "1m")]
    [InlineData(135_000, "2m15s")]
    [InlineData(180_000, "3m")]
    public void FormatWaitClock_FormatsCompactMinSec(long ms, string expected)
    {
        TerminalPageRenderer.FormatWaitClock(ms).Should().Be(expected);
    }

    [Fact]
    public void RenderManualLogin_WithElapsedAndTimeout_ShowsWaitClock()
    {
        var output = CaptureRender(r => r.RenderManualLogin(
            "https://example.com/login", "example.com", elapsedMs: 135_000, timeoutMs: 180_000));

        output.Should().Contain("Login required for example.com");
        output.Should().Contain("2m15s elapsed",
            "the user must see how long they have been waiting");
        output.Should().Contain("(3m timeout)",
            "the user must see how long the app will keep waiting");
    }

    [Fact]
    public void RenderManualLogin_WithoutClockArgs_KeepsLegacyWaitingLine()
    {
        var output = CaptureRender(r => r.RenderManualLogin(
            "https://example.com/login", "example.com"));

        output.Should().Contain("Waiting for login to complete...");
        output.Should().NotContain("timeout");
    }

    [Fact]
    public void RenderManualLogin_SpinnerFrameAdvancesWithElapsedTime()
    {
        // Two renders 500ms of "elapsed" apart must not paint the identical
        // waiting line — the spinner frame moves, which is the visible proof
        // of life during the poll loop.
        var first = CaptureRender(r => r.RenderManualLogin(
            "https://example.com/login", "example.com", elapsedMs: 0, timeoutMs: 180_000));
        var second = CaptureRender(r => r.RenderManualLogin(
            "https://example.com/login", "example.com", elapsedMs: 500, timeoutMs: 180_000));

        second.Should().NotBe(first, "the spinner frame must advance between poll ticks");
    }

    private static string CaptureRender(Action<TerminalPageRenderer> action)
    {
        var themeProvider = Substitute.For<IThemeProvider>();
        themeProvider.CurrentTheme.Returns(ThemeName.Phosphor);
        var renderer = new TerminalPageRenderer(
            themeProvider, Substitute.For<ILogger<TerminalPageRenderer>>());

        var originalOut = Console.Out;
        try
        {
            using var sw = new StringWriter();
            Console.SetOut(sw);
            action(renderer);
            return sw.ToString();
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }
}
