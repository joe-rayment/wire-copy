// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Domain.Entities.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Browser.UI;
using WireCopy.Infrastructure.Browser.UI.Components;
using WireCopy.Infrastructure.Browser.UI.Renderers;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// workspace-9k27.14 — the safety net that ends the stale-hint bug class.
///
/// <para>Key labels used to be free string literals disjoint from the dispatch switch,
/// so an advertised key could rot silently (the Shift+L class of bug). These tests make
/// that impossible by driving the REAL dispatch in
/// <see cref="TerminalInputHandler"/> — no source-text parsing:</para>
/// <list type="number">
/// <item><see cref="KeyCharSwitch_DoesNotShadowAnyShiftBinding"/> makes the
/// KeyChar-before-Shift trap a hard failure: any capital the KeyChar switch claims must
/// NOT also be claimed by the Shift-modifier switch.</item>
/// <item><see cref="EveryRegistryLabel_ResolvesToItsOwnCommand"/> proves the declared
/// <see cref="KeyRegistry"/> label for a command actually dispatches to that command.</item>
/// <item><see cref="EveryAdvertisedPopupKeystroke_ResolvesToNonNoOp"/> and
/// <see cref="EveryAdvertisedHintKeystroke_ResolvesToNonNoOp"/> prove every key the user
/// is TOLD about (popup + status hint tiers) maps to a live, non-NoOp binding.</item>
/// </list>
/// </summary>
[Trait("Category", "Unit")]
public class KeyRegistryEnforcementTests
{
    // ---- (1) Disjointness: the KeyChar switch must never shadow a Shift binding ----

    [Fact]
    public void KeyCharSwitch_DoesNotShadowAnyShiftBinding()
    {
        // The KeyChar switch (MapKeyInfoToCommand) runs BEFORE the Shift-modifier
        // switch (MapKeyToCommandStatic). A capital letter claimed by the former
        // therefore silently eats Shift+<that letter> — exactly how Shift+L died.
        // Probe with Key=NoName so the fallback returns NoOp: a non-NoOp result means
        // the KeyChar switch itself claimed the capital.
        for (var c = 'A'; c <= 'Z'; c++)
        {
            var viaChar = TerminalInputHandler.MapKeyInfoToCommand(
                new ConsoleKeyInfo(c, ConsoleKey.NoName, false, false, false));

            if (viaChar.Type == CommandType.NoOp)
            {
                continue; // the KeyChar switch does not claim this capital
            }

            var key = ConsoleKey.A + (c - 'A');
            var viaShift = TerminalInputHandler.MapKeyToCommandStatic(key, ConsoleModifiers.Shift);

            viaShift.Type.Should().Be(
                CommandType.NoOp,
                $"'{c}' is handled by the KeyChar switch, so a Shift+{c} binding in the modifier "
                + $"switch (currently {viaShift.Type}) would be silently shadowed — the Shift+L trap");
        }
    }

    // ---- (2) Registry labels are the declared truth; dispatch is the executed truth ----

    [Fact]
    public void EveryRegistryLabel_ResolvesToItsOwnCommand()
    {
        foreach (var (command, label) in KeyRegistry.All)
        {
            Resolve(label).Should().Be(
                command,
                $"KeyRegistry advertises '{label}' for {command}; the real dispatch must resolve it back");
        }
    }

    // ---- (3) Nothing advertised to the user may be a dead key ----

    [Theory]
    [InlineData(ViewMode.Hierarchical)]
    [InlineData(ViewMode.Readable)]
    [InlineData(ViewMode.CollectionList)]
    [InlineData(ViewMode.CollectionItems)]
    [InlineData(ViewMode.Launcher)]
    public void EveryAdvertisedPopupKeystroke_ResolvesToNonNoOp(ViewMode mode)
    {
        foreach (var (key, _) in KeybindingPopup.GetBindings(mode))
        {
            foreach (var token in Tokenize(key))
            {
                Resolve(token).Should().NotBe(
                    CommandType.NoOp,
                    $"popup key '{key}' (token '{token}') in {mode} advertises a binding that must dispatch");
            }
        }
    }

    [Fact]
    public void EveryAdvertisedHintKeystroke_ResolvesToNonNoOp()
    {
        var modes = new[]
        {
            ViewMode.Hierarchical, ViewMode.Readable, ViewMode.CollectionList, ViewMode.CollectionItems,
        };

        foreach (var mode in modes)
        {
            foreach (var tier in AllHintTiers(mode))
            {
                foreach (var (key, _) in tier)
                {
                    foreach (var token in Tokenize(key))
                    {
                        Resolve(token).Should().NotBe(
                            CommandType.NoOp,
                            $"hint key '{key}' (token '{token}') in {mode} advertises a binding that must dispatch");
                    }
                }
            }
        }
    }

    // ---- Enumerate every hint tier, including the state-sensitive ones ----

    private static IEnumerable<(string Key, string Action)[]> AllHintTiers(ViewMode mode)
    {
        foreach (var tier in StatusBarRenderer.GetHintTiers(mode))
        {
            yield return tier;
        }

        foreach (var tier in StatusBarRenderer.GetHintTiers(mode, preloadDetailVisible: true))
        {
            yield return tier;
        }

        if (mode == ViewMode.Readable)
        {
            foreach (var tier in StatusBarRenderer.GetHintTiers(mode, SpeedReadContext()))
            {
                yield return tier;
            }
        }

        if (mode == ViewMode.Hierarchical)
        {
            foreach (var tier in StatusBarRenderer.GetHintTiers(mode, SelectionContext(2)))
            {
                yield return tier;
            }
        }
    }

    private static NavigationContext SpeedReadContext() => new()
    {
        ViewMode = ViewMode.Readable,
        IsSpeedReadActive = true,
        SpeedReadWpm = 350,
    };

    private static NavigationContext SelectionContext(int selectionCount)
    {
        var page = Page.Create(
            "https://example.com",
            "<html></html>",
            new PageMetadata { Title = "Test" });
        var links = Enumerable.Range(1, selectionCount)
            .Select(i => new LinkInfo
            {
                DisplayText = $"Link {i}",
                Url = $"https://example.com/{i}",
                Type = LinkType.Content,
                ImportanceScore = 1,
            })
            .ToList();
        var tree = NavigationTree.Build(links);
        foreach (var node in tree.GetAllNodes().Take(selectionCount))
        {
            tree.SelectedNodeIds.Add(node.Id);
        }

        page.SetLinkTree(tree);
        return new NavigationContext { ViewMode = ViewMode.Hierarchical, CurrentPage = page };
    }

    // ---- The resolver: turns an advertised label token into the command the REAL
    //      dispatch produces for it. Drives TerminalInputHandler's actual switches. ----

    private static IEnumerable<string> Tokenize(string label)
    {
        // Composite advertisements ("j / k", "b / B", "q / Ctrl+C") teach multiple keys.
        // Split on the separators the labels use; chords ("gg", "g l") and combined
        // glyphs ("[]", "</>") stay whole for Resolve to handle.
        var parts = label.Split(
            new[] { " / ", " or " },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var part in parts)
        {
            var token = part.Trim();
            if (token.Length == 0)
            {
                continue;
            }

            // ":new", ":rename <x>" etc. are command-line verbs, not keystrokes; the bare
            // ":" (open command line) IS a keystroke and is kept.
            if (token.Length > 1 && token[0] == ':')
            {
                continue;
            }

            yield return token;
        }
    }

    private static CommandType Resolve(string token)
    {
        token = token.Trim();

        switch (token)
        {
            // Goto chords resolved via the same pure helper the input loop uses.
            case "gg":
                return TerminalInputHandler.ResolveGotoChord(Ki('g', ConsoleKey.G)).Type;
            case "g l":
                return TerminalInputHandler.ResolveGotoChord(Ki('l', ConsoleKey.L)).Type;
            case "g s":
                return TerminalInputHandler.ResolveGotoChord(Ki('s', ConsoleKey.S)).Type;

            // The launcher digit-jump is dispatched in the stateful input loop (gated on
            // launcher mode + no count prefix), not a pure map, so it is resolved here by
            // declaration. It is not a collision candidate — no other binding claims 1-9.
            case "1-9":
                return CommandType.JumpToIndex;

            case "Enter":
                return Dispatch(Ki('\r', ConsoleKey.Enter));
            case "Esc":
            case "Escape":
                return Dispatch(Ki('\x1b', ConsoleKey.Escape));
            case "Space":
                return Dispatch(Ki(' ', ConsoleKey.Spacebar));
            case "Tab":
                return Dispatch(Ki('\t', ConsoleKey.Tab));
            case "Backspace":
                return Dispatch(Ki('\b', ConsoleKey.Backspace));
            case "F5":
                return Dispatch(Ki('\0', ConsoleKey.F5));
            case "↑":
                return Dispatch(Ki('\0', ConsoleKey.UpArrow));
            case "↓":
                return Dispatch(Ki('\0', ConsoleKey.DownArrow));
            case "←":
                return Dispatch(Ki('\0', ConsoleKey.LeftArrow));
            case "→":
                return Dispatch(Ki('\0', ConsoleKey.RightArrow));
        }

        // Modifier combos: "Ctrl+D", "Shift+R", "Ctrl+C".
        if (token.Contains('+'))
        {
            var pieces = token.Split('+');
            var mod = pieces[0].Trim();
            var basePart = pieces[^1].Trim();
            if (basePart.Length != 1)
            {
                return CommandType.NoOp;
            }

            var c = basePart[0];
            var key = LetterKey(c);
            if (mod.Equals("Ctrl", StringComparison.OrdinalIgnoreCase))
            {
                // Ctrl+<letter> arrives with a control KeyChar the switch ignores.
                return Dispatch(Ki('\0', key, control: true));
            }

            if (mod.Equals("Shift", StringComparison.OrdinalIgnoreCase))
            {
                // Shift+<letter> arrives as the uppercase KeyChar — faithfully reproduces
                // the KeyChar-switch shadow risk.
                return Dispatch(Ki(char.ToUpperInvariant(c), key, shift: true));
            }

            return CommandType.NoOp;
        }

        if (token.Length == 1)
        {
            return DispatchChar(token[0]);
        }

        // Combined-glyph token like "[]" or "</>": every glyph must resolve.
        foreach (var c in token)
        {
            if (DispatchChar(c) == CommandType.NoOp)
            {
                return CommandType.NoOp;
            }
        }

        return DispatchChar(token[0]);
    }

    private static CommandType DispatchChar(char c)
    {
        // A bare capital LETTER only reaches the app as Shift+letter, so it carries the
        // Shift modifier and an uppercase KeyChar. Punctuation and digits carry neither.
        var isUpperLetter = char.IsLetter(c) && char.IsUpper(c);
        var key = char.IsLetter(c) ? LetterKey(c) : ConsoleKey.NoName;
        return Dispatch(new ConsoleKeyInfo(c, key, isUpperLetter, false, false));
    }

    private static CommandType Dispatch(ConsoleKeyInfo keyInfo)
        => TerminalInputHandler.MapKeyInfoToCommand(keyInfo).Type;

    private static ConsoleKeyInfo Ki(char ch, ConsoleKey key, bool shift = false, bool control = false)
        => new(ch, key, shift, alt: false, control);

    private static ConsoleKey LetterKey(char c) => ConsoleKey.A + (char.ToUpperInvariant(c) - 'A');
}
