// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Infrastructure.Browser.UI.Renderers;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// workspace-8fkv: a left-docked browser covers part of the terminal, so the app shifts its
/// whole frame into the uncovered columns via <see cref="RenderHelpers.ColumnOffset"/>.
/// WriteLineCore clears the full line (so the browser sits over blanked columns) then moves
/// the cursor to ColumnOffset (+ any per-view LeftMargin) before the body. These assert the
/// shift on the REAL render chokepoint every view writes through. In the serial console
/// collection because EndFrame flushes to Console.Out.
/// </summary>
[Trait("Category", "Unit")]
[Collection(WireCopy.Tests.ConsoleSerialCollection.Name)]
public class RenderHelpersDockOffsetTests
{
    private static string CaptureFrame(System.Action<RenderHelpers> render)
    {
        var originalOut = System.Console.Out;
        try
        {
            using var sw = new System.IO.StringWriter();
            System.Console.SetOut(sw);
            var helpers = new RenderHelpers { TerminalHeight = 24 };
            helpers.BeginFrame();
            render(helpers);
            helpers.EndFrame();
            return sw.ToString();
        }
        finally
        {
            System.Console.SetOut(originalOut);
        }
    }

    [Fact]
    public void WriteLine_WithColumnOffset_ShiftsFirstLineToOffsetColumn()
    {
        var output = CaptureFrame(h =>
        {
            h.ColumnOffset = 20;
            h.WriteLine("hello");
        });

        // CUP is 1-based, so column 20 emits \x1b[1;21H before the body on the first line.
        output.Should().Contain("\x1b[1;21H");
        output.Should().Contain("hello");
    }

    [Fact]
    public void WriteLine_WithoutColumnOffset_DoesNotShift()
    {
        var output = CaptureFrame(h => h.WriteLine("hello"));

        // No offset and no margin: content stays flush-left; only the column-1 clear is emitted.
        output.Should().Contain("\x1b[1;1H");
        output.Should().NotContain("\x1b[1;21H");
    }

    [Fact]
    public void WriteLine_AddsColumnOffsetToLeftMargin_RatherThanOverwriting()
    {
        var output = CaptureFrame(h =>
        {
            h.ColumnOffset = 20;
            h.LeftMargin = 5; // e.g. reader centering — must compose with the dock offset
            h.WriteLine("x");
        });

        // start column = ColumnOffset(20) + LeftMargin(5) = 25 → \x1b[1;26H
        output.Should().Contain("\x1b[1;26H");
    }

    [Fact]
    public void Clear_ResetsColumnOffset_SoItNeverLeaksAcrossFrames()
    {
        var originalOut = System.Console.Out;
        try
        {
            using var sw = new System.IO.StringWriter();
            System.Console.SetOut(sw);
            var helpers = new RenderHelpers { ColumnOffset = 30, TerminalHeight = 24 };
            helpers.BeginFrame();
            helpers.Clear();
            helpers.EndFrame();
            helpers.ColumnOffset.Should().Be(0);
        }
        finally
        {
            System.Console.SetOut(originalOut);
        }
    }
}
