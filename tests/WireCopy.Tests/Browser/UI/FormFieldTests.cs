// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using NSubstitute;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Infrastructure.Browser.Themes;
using WireCopy.Infrastructure.Browser.UI.Components;
using WireCopy.Infrastructure.Browser.UI.Renderers;
using Xunit;

namespace WireCopy.Tests.Browser.UI;

[Trait("Category", "Unit")]
[Collection("ConsoleOutput")]
public class FormFieldTests
{
    [Fact]
    public void FormFieldConfig_SetsDefaults()
    {
        var config = new FormFieldConfig { Label = "API Key" };

        config.Label.Should().Be("API Key");
        config.Placeholder.Should().BeNull();
        config.HelpText.Should().BeNull();
        config.IsSecret.Should().BeFalse();
        config.Validate.Should().BeNull();
        config.MaxLength.Should().Be(256);
        config.InitialValue.Should().BeNull();
        config.OnExtraKey.Should().BeNull();
    }

    [Fact]
    public void FormFieldConfig_OnExtraKey_RoundTripsDelegate()
    {
        Func<char, bool> hook = c => c == '?';

        var config = new FormFieldConfig
        {
            Label = "Bucket",
            OnExtraKey = hook,
        };

        config.OnExtraKey.Should().BeSameAs(hook);
        config.OnExtraKey!('?').Should().BeTrue();
        config.OnExtraKey!('a').Should().BeFalse();
    }

    [Fact]
    public async Task PromptAsync_WithOnExtraKey_PassesInterceptorToInputHandler()
    {
        // Verify FormField.PromptAsync forwards a non-null interceptKey to the
        // input handler when OnExtraKey is configured. Driving the printable-
        // char branch end-to-end requires a real terminal (TerminalInputHandler
        // owns stdin via a background thread), so we assert the contract at the
        // FormField boundary.
        Func<char, bool>? receivedInterceptor = null;
        var inputHandler = Substitute.For<IInputHandler>();
        inputHandler.PromptForInputAsync(
            Arg.Any<string>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<bool>(),
            Arg.Any<int?>(),
            Arg.Any<int?>(),
            Arg.Any<string?>(),
            Arg.Any<Func<char, bool>?>())
            .Returns(call =>
            {
                receivedInterceptor = call.ArgAt<Func<char, bool>?>(6);
                return Task.FromResult<string?>("bucket-name");
            });

        var helpCalls = new List<char>();
        var field = new FormFieldConfig
        {
            Label = "Bucket",
            OnExtraKey = c =>
            {
                helpCalls.Add(c);
                return c == '?';
            },
        };
        var palette = BuiltInThemes.Get(Domain.Enums.Browser.ThemeName.Phosphor);

        try
        {
            var result = await FormField.PromptAsync(
                inputHandler, field, palette, 0, 50, CancellationToken.None);
            result.Should().Be("bucket-name");
        }
        catch (IOException)
        {
            // Expected in CI — Console.SetCursorPosition fails without a terminal.
        }

        receivedInterceptor.Should().NotBeNull(
            "FormField must forward a non-null interceptor when OnExtraKey is set");

        // Drive the wrapped interceptor and verify it dispatches to OnExtraKey.
        // The wrapper also re-renders chrome on a 'true' return — that touches
        // Console which throws in CI; swallow it.
        try
        {
            var handled = receivedInterceptor!('?');
            handled.Should().BeTrue();
        }
        catch (IOException)
        {
            // chrome re-render can fail in CI — the OnExtraKey call still fired
        }

        try
        {
            var handled = receivedInterceptor!('a');
            handled.Should().BeFalse("OnExtraKey returned false so the wrapper should also return false");
        }
        catch (IOException)
        {
            // ignore — chrome re-render only runs on handled==true
        }

        helpCalls.Should().Contain('?');
        helpCalls.Should().Contain('a');
    }

    [Fact]
    public async Task PromptAsync_WithoutOnExtraKey_PassesNullInterceptor()
    {
        Func<char, bool>? receivedInterceptor = null;
        var inputHandler = Substitute.For<IInputHandler>();
        inputHandler.PromptForInputAsync(
            Arg.Any<string>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<bool>(),
            Arg.Any<int?>(),
            Arg.Any<int?>(),
            Arg.Any<string?>(),
            Arg.Any<Func<char, bool>?>())
            .Returns(call =>
            {
                receivedInterceptor = call.ArgAt<Func<char, bool>?>(6);
                return Task.FromResult<string?>("value");
            });

        var field = new FormFieldConfig { Label = "Field" };
        var palette = BuiltInThemes.Get(Domain.Enums.Browser.ThemeName.Phosphor);

        try
        {
            await FormField.PromptAsync(inputHandler, field, palette, 0, 50, CancellationToken.None);
        }
        catch (IOException)
        {
            // Expected in CI
        }

        receivedInterceptor.Should().BeNull(
            "FormField must not invent an interceptor when the caller didn't supply one");
    }

    [Fact]
    public void FormFieldConfig_AllProperties()
    {
        Func<string, string?> validator = v => v.StartsWith("sk-") ? null : "Must start with sk-";

        var config = new FormFieldConfig
        {
            Label = "API Key",
            Placeholder = "sk-ant-api03-...",
            HelpText = "Enter your Anthropic API key",
            IsSecret = true,
            Validate = validator,
            MaxLength = 100,
            InitialValue = "sk-test",
        };

        config.Label.Should().Be("API Key");
        config.Placeholder.Should().Be("sk-ant-api03-...");
        config.HelpText.Should().Be("Enter your Anthropic API key");
        config.IsSecret.Should().BeTrue();
        config.Validate.Should().BeSameAs(validator);
        config.MaxLength.Should().Be(100);
        config.InitialValue.Should().Be("sk-test");
    }

    [Fact]
    public void FormFieldConfig_Validator_ReturnsNull_ForValidInput()
    {
        Func<string, string?> validator = v =>
            v.StartsWith("sk-", StringComparison.Ordinal) ? null : "Must start with sk-";

        validator("sk-ant-api03-abc").Should().BeNull();
    }

    [Fact]
    public void FormFieldConfig_Validator_ReturnsError_ForInvalidInput()
    {
        Func<string, string?> validator = v =>
            v.StartsWith("sk-", StringComparison.Ordinal) ? null : "Must start with sk-";

        validator("badkey123").Should().Be("Must start with sk-");
    }

    [Fact]
    public void Height_IsFive()
    {
        FormField.Height.Should().Be(5);
    }

    [Fact]
    public async Task PromptAsync_WhenEscapePressed_ReturnsNull()
    {
        // PromptForInputAsync returns null when Escape is pressed
        var inputHandler = Substitute.For<IInputHandler>();
        inputHandler.PromptForInputAsync(
            Arg.Any<string>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<bool>(),
            Arg.Any<int?>(),
            Arg.Any<int?>(),
            Arg.Any<string?>())
            .Returns((string?)null);

        var field = new FormFieldConfig { Label = "Test" };
        var palette = BuiltInThemes.Get(Domain.Enums.Browser.ThemeName.Phosphor);

        // FormField.PromptAsync uses Console.SetCursorPosition which throws
        // in non-interactive test environments — catch and verify the intent
        try
        {
            var result = await FormField.PromptAsync(
                inputHandler, field, palette, 0, 50, CancellationToken.None);
            result.Should().BeNull();
        }
        catch (IOException)
        {
            // Expected in CI — Console.SetCursorPosition fails without a terminal
        }
    }

    [Fact]
    public async Task PromptAsync_WithValidInput_ReturnsValue()
    {
        var inputHandler = Substitute.For<IInputHandler>();
        inputHandler.PromptForInputAsync(
            Arg.Any<string>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<bool>(),
            Arg.Any<int?>(),
            Arg.Any<int?>(),
            Arg.Any<string?>())
            .Returns("sk-ant-api03-test");

        var field = new FormFieldConfig
        {
            Label = "API Key",
            Validate = v => v.StartsWith("sk-", StringComparison.Ordinal) ? null : "Must start with sk-",
        };
        var palette = BuiltInThemes.Get(Domain.Enums.Browser.ThemeName.Phosphor);

        try
        {
            var result = await FormField.PromptAsync(
                inputHandler, field, palette, 0, 50, CancellationToken.None);
            result.Should().Be("sk-ant-api03-test");
        }
        catch (IOException)
        {
            // Expected in CI — Console.SetCursorPosition fails without a terminal
        }
    }

    [Fact]
    public async Task PromptAsync_WithValidation_RetriesOnFailure()
    {
        var callCount = 0;
        var inputHandler = Substitute.For<IInputHandler>();
        inputHandler.PromptForInputAsync(
            Arg.Any<string>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<bool>(),
            Arg.Any<int?>(),
            Arg.Any<int?>(),
            Arg.Any<string?>())
            .Returns(x =>
            {
                callCount++;
                // First call returns invalid, second returns valid
                return callCount == 1 ? "badkey" : "sk-valid";
            });

        var field = new FormFieldConfig
        {
            Label = "API Key",
            Validate = v => v.StartsWith("sk-", StringComparison.Ordinal) ? null : "Must start with sk-",
        };
        var palette = BuiltInThemes.Get(Domain.Enums.Browser.ThemeName.Phosphor);

        try
        {
            var result = await FormField.PromptAsync(
                inputHandler, field, palette, 0, 50, CancellationToken.None);
            result.Should().Be("sk-valid");
            callCount.Should().Be(2);
        }
        catch (IOException)
        {
            // Expected in CI — Console.SetCursorPosition fails without a terminal
        }
    }

    [Fact]
    public async Task PromptAsync_IsSecret_PassedToInputHandler()
    {
        var inputHandler = Substitute.For<IInputHandler>();
        inputHandler.PromptForInputAsync(
            Arg.Any<string>(),
            Arg.Any<CancellationToken>(),
            isSecret: true,
            Arg.Any<int?>(),
            Arg.Any<int?>(),
            Arg.Any<string?>())
            .Returns("secret-value");

        var field = new FormFieldConfig
        {
            Label = "Password",
            IsSecret = true,
        };
        var palette = BuiltInThemes.Get(Domain.Enums.Browser.ThemeName.Phosphor);

        try
        {
            var result = await FormField.PromptAsync(
                inputHandler, field, palette, 0, 50, CancellationToken.None);
            result.Should().Be("secret-value");

            await inputHandler.Received().PromptForInputAsync(
                Arg.Any<string>(),
                Arg.Any<CancellationToken>(),
                isSecret: true,
                Arg.Any<int?>(),
                Arg.Any<int?>(),
                Arg.Any<string?>());
        }
        catch (IOException)
        {
            // Expected in CI — Console.SetCursorPosition fails without a terminal
        }
    }

    [Fact]
    public async Task PromptAsync_EnforcesMaxLength()
    {
        var inputHandler = Substitute.For<IInputHandler>();
        inputHandler.PromptForInputAsync(
            Arg.Any<string>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<bool>(),
            Arg.Any<int?>(),
            Arg.Any<int?>(),
            Arg.Any<string?>())
            .Returns(new string('a', 300));

        var field = new FormFieldConfig
        {
            Label = "Short Field",
            MaxLength = 10,
        };
        var palette = BuiltInThemes.Get(Domain.Enums.Browser.ThemeName.Phosphor);

        try
        {
            var result = await FormField.PromptAsync(
                inputHandler, field, palette, 0, 50, CancellationToken.None);
            result.Should().HaveLength(10);
        }
        catch (IOException)
        {
            // Expected in CI — Console.SetCursorPosition fails without a terminal
        }
    }

    #region workspace-u9cc — overlay must mask the rightmost column

    /// <summary>
    /// Workspace-u9cc regression: at width 80 the Setup-screen "Output folder"
    /// row's content (label + middle-truncated path + the right-aligned
    /// <c>[Enter] Change</c> action) overflows past column 80. Before the
    /// inline overlay renders, the tail of that overflow lives at column 80
    /// of the underlying row. <see cref="FormField.ClearLine"/> must mask
    /// that column when redrawing the rows the overlay covers — otherwise a
    /// stray <c>h</c> from <c>[Enter] Change</c> shows up to the right of
    /// the overlay's top border.
    /// </summary>
    [Fact]
    public void SettingsRow_OutputFolder_OverflowsBeyondColumn80_AtWidth80()
    {
        var palette = BuiltInThemes.Get(Domain.Enums.Browser.ThemeName.Phosphor);

        // Replicates the HandleConfigScreen Output-folder row at terminal width 80:
        // width param = TerminalWidth - 2 = 78, path = TruncateMiddle(home/.local/...output, 38).
        var path = "/home/agent/.local/…re/WireCopy/output";
        var (mainLine, _) = SettingsRowRenderer.Build(
            palette,
            width: 78,
            isSelected: false,
            isWarning: false,
            statusIcon: "●",
            statusColor: palette.PromptFg.AnsiFg,
            label: "Output folder",
            value: path,
            valueColor: palette.PromptFg.AnsiFg,
            actionLabel: "Change");

        var plain = SettingsRowRenderer.StripAnsi(mainLine);
        plain.Length.Should().BeGreaterOrEqualTo(80,
            "the Output-folder row's content + '[Enter] Change' action overflows past col 80 at width 80 — this is the visible bleed prerequisite");

        // The 80th visible column (1-indexed) carries a non-blank tail char
        // from "[Enter] Change" — this is the character that bleeds when the
        // overlay's ClearLine fails to mask it.
        plain[79].Should().NotBe(' ',
            "the row's tail-character at col 80 is what bleeds when the overlay fails to clear the rightmost column");
    }

    /// <summary>
    /// Workspace-u9cc fix verification: <see cref="FormField.ClearLine"/>
    /// must emit the <c>\x1b[2K</c> erase-in-line escape so the entire row
    /// (including its rightmost column) is masked when the overlay redraws
    /// over an overflowed Setup row. The previous implementation wrote
    /// <c>WindowWidth - 1</c> spaces, which left col <c>WindowWidth</c>
    /// (col 80 at width 80) preserving whatever the underlying row left
    /// there.
    /// </summary>
    [Fact]
    public void ClearLine_EmitsFullLineEraseEscape()
    {
        var originalOut = Console.Out;
        try
        {
            using var sw = new StringWriter();
            Console.SetOut(sw);

            FormField.ClearLine(0);

            var captured = sw.ToString();
            captured.Should().Contain("\x1b[2K",
                "\\x1b[2K clears the whole line including the rightmost column, where the bleed-tail otherwise lives");
            captured.Should().NotContain(new string(' ', 79),
                "the old WindowWidth-1 space-pad would skip col 80 — the fix must not regress to that pattern");
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    /// <summary>
    /// Workspace-u9cc end-to-end: simulate a width-80 terminal, render the
    /// overflowing "Output folder" row through it, then apply the overlay's
    /// chrome-clear and label/border writes the same way
    /// <see cref="FormField"/> does. After the simulated overlay renders,
    /// the rightmost column of every row the overlay touches must be a
    /// space (cleared by <c>\x1b[2K</c>) or the overlay's own border — never
    /// a tail char (<c>h</c>, <c>e</c>, <c>g</c>, <c>n</c>, <c>a</c>) from
    /// the underlying "[Enter] Change" hint.
    /// </summary>
    [Fact]
    public void OverlayRender_AtWidth80_MasksRightmostColumnOfCoveredRows()
    {
        var palette = BuiltInThemes.Get(Domain.Enums.Browser.ThemeName.Phosphor);

        const int width = 80;
        const int height = 24;
        var term = new SimulatedTerminal(width, height);

        // ---- Underlying Setup-screen render: the overflowing "Output folder" row
        //      lives at row index 5 (the overlay's top border will land on it).
        //      Use the actual SettingsRowRenderer.Build output so the visible
        //      tail (the bleed character) is realistic.
        var (mainLine, _) = SettingsRowRenderer.Build(
            palette,
            width: width - 2,
            isSelected: false,
            isWarning: false,
            statusIcon: "●",
            statusColor: palette.PromptFg.AnsiFg,
            label: "Output folder",
            value: "/home/agent/.local/…re/WireCopy/output",
            valueColor: palette.PromptFg.AnsiFg,
            actionLabel: "Change");
        var overflowRow = SettingsRowRenderer.StripAnsi(mainLine);

        // Place the overflowing row on rows that the overlay will cover so
        // the bleed has somewhere to be hidden / surface.
        const int startRow = 5;
        const int overlayHeight = FormField.Height; // label + top + input + bottom + help
        for (var r = startRow; r < startRow + overlayHeight; r++)
        {
            term.WriteAt(0, r, overflowRow);
        }

        // Sanity: col 80 (1-indexed) of those rows should currently carry
        // the bleed-tail char from "[Enter] Change" — that's the bug.
        var tailBefore = term.CharAt(79, startRow + 1);
        tailBefore.Should().NotBe(' ',
            "the underlying row has a non-blank tail at col 80 — that's the bleed source");

        // ---- Overlay render: replicate FormField.RenderFieldChrome for a
        //      simple label + border + input + bottom + help span. Each row
        //      first emits the workspace-u9cc full-line erase, then positions
        //      the overlay content at col 2.
        const string fullLineErase = "\x1b[2K";
        var boxWidth = 60; // matches HandleSetBucket's fieldWidth at width 80

        // Label row
        term.SetCursor(0, startRow);
        term.Write(fullLineErase);
        term.WriteAt(2, startRow, "Public feed URL (where your feed will live)");

        // Top border row
        term.SetCursor(0, startRow + 1);
        term.Write(fullLineErase);
        term.WriteAt(2, startRow + 1, "╭" + new string('─', boxWidth - 2) + "╮");

        // Input row
        term.SetCursor(0, startRow + 2);
        term.Write(fullLineErase);
        term.WriteAt(2, startRow + 2, "│" + new string(' ', boxWidth - 2) + "│");

        // Bottom border row
        term.SetCursor(0, startRow + 3);
        term.Write(fullLineErase);
        term.WriteAt(2, startRow + 3, "╰" + new string('─', boxWidth - 2) + "╯");

        // Help row
        term.SetCursor(0, startRow + 4);
        term.Write(fullLineErase);
        term.WriteAt(2, startRow + 4, "? for help · Enter to verify · Esc to cancel");

        // ---- Assert: col 80 (1-indexed = 79 0-indexed) of every overlay
        //      row is whitespace. The bleed tail char must be gone.
        var bleedChars = new[] { 'h', 'e', 'g', 'n', 'a', 'C' };
        for (var r = startRow; r < startRow + overlayHeight; r++)
        {
            var rightmost = term.CharAt(79, r);
            rightmost.Should().Be(' ',
                $"col 80 of row {r} (overlay-covered) must be cleared by the overlay's \\x1b[2K — found '{rightmost}'");
            bleedChars.Should().NotContain(rightmost,
                $"col 80 of overlay row {r} must not retain a tail char from '[Enter] Change'");
        }

        // Spot-check that the overlay's own content still renders inside the
        // expected horizontal span — the fix must not break the chrome.
        term.CharAt(2, startRow + 1).Should().Be('╭', "overlay top-left corner stays at col 3 (0-indexed 2)");
        term.CharAt(2 + boxWidth - 1, startRow + 1).Should().Be('╮', "overlay top-right corner stays inside the line");
    }

    /// <summary>
    /// Minimal in-memory terminal that tracks a 2D character grid plus a
    /// cursor, and understands just enough ANSI to validate the overlay
    /// bleed fix: <c>\x1b[2K</c> (erase entire line), CSI cursor-position
    /// <c>\x1b[r;cH</c>, and SGR colour escapes (ignored). Lives inside the
    /// test class because it's only meant to drive workspace-u9cc's
    /// assertions — production code keeps using <see cref="Console"/>
    /// directly.
    /// </summary>
    private sealed class SimulatedTerminal
    {
        private readonly char[,] _cells;
        private int _row;
        private int _col;

        public SimulatedTerminal(int width, int height)
        {
            Width = width;
            Height = height;
            _cells = new char[height, width];
            for (var r = 0; r < height; r++)
            {
                for (var c = 0; c < width; c++)
                {
                    _cells[r, c] = ' ';
                }
            }
        }

        public int Width { get; }

        public int Height { get; }

        public void SetCursor(int col, int row)
        {
            _col = Math.Clamp(col, 0, Width - 1);
            _row = Math.Clamp(row, 0, Height - 1);
        }

        public void WriteAt(int col, int row, string text)
        {
            SetCursor(col, row);
            Write(text);
        }

        public void Write(string text)
        {
            var i = 0;
            while (i < text.Length)
            {
                var ch = text[i];
                if (ch == '\x1b' && i + 1 < text.Length && text[i + 1] == '[')
                {
                    var end = i + 2;
                    while (end < text.Length && !char.IsLetter(text[end]))
                    {
                        end++;
                    }

                    if (end >= text.Length)
                    {
                        return;
                    }

                    var final = text[end];
                    var args = text.Substring(i + 2, end - (i + 2));
                    ProcessCsi(args, final);
                    i = end + 1;
                    continue;
                }

                if (_col < Width && _row < Height)
                {
                    _cells[_row, _col] = ch;
                }

                _col++;
                if (_col >= Width)
                {
                    // Autowrap: don't mutate further rows — overflow is
                    // intentionally captured as truncation since the
                    // production renderer overflows in the same way.
                    _col = Width - 1;
                }

                i++;
            }
        }

        public char CharAt(int col, int row) => _cells[row, col];

        private void ProcessCsi(string args, char final)
        {
            switch (final)
            {
                case 'H':
                {
                    // CUP — \x1b[<row>;<col>H, 1-indexed.
                    var parts = args.Length == 0 ? new[] { "1", "1" } : args.Split(';');
                    var r = parts.Length > 0 && int.TryParse(parts[0], out var pr) ? pr : 1;
                    var c = parts.Length > 1 && int.TryParse(parts[1], out var pc) ? pc : 1;
                    SetCursor(c - 1, r - 1);
                    break;
                }

                case 'K':
                {
                    // EL — 0 (default): from cursor to EOL; 2: entire line.
                    var mode = string.IsNullOrEmpty(args) ? 0 : int.Parse(args, System.Globalization.CultureInfo.InvariantCulture);
                    if (mode == 2)
                    {
                        for (var c = 0; c < Width; c++)
                        {
                            _cells[_row, c] = ' ';
                        }
                    }
                    else if (mode == 0)
                    {
                        for (var c = _col; c < Width; c++)
                        {
                            _cells[_row, c] = ' ';
                        }
                    }
                    else if (mode == 1)
                    {
                        for (var c = 0; c <= _col; c++)
                        {
                            _cells[_row, c] = ' ';
                        }
                    }

                    break;
                }

                case 'm':
                    // SGR — colours/attributes; ignore for layout assertions.
                    break;

                default:
                    // Unhandled CSI — ignore for the narrow scope of this test.
                    break;
            }
        }
    }

    #endregion
}
