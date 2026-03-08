// Educational and personal use only.

using System.Text;
using TermReader.Application.DTOs.Browser;
using TermReader.Application.Interfaces.Browser;
using TermReader.Infrastructure.Browser.Themes;

namespace TermReader.Infrastructure.Browser.UI.Renderers;

/// <summary>
/// Renders the podcast call-to-action button in collection views.
/// Three tiers: Full Slab (5 lines), Compact Slab (3 lines), Inline (1 line).
/// </summary>
internal class PodcastCtaRenderer
{
    private const string Reset = "\x1b[0m";
    private const string Bold = "\x1b[1m";
    private const string Dim = "\x1b[2m";
    private const string PlayIcon = "\u25b6\u25b6";
    private const string Label = "Generate Podcast";
    private const string KeyHint = "[p]";
    private const int MinButtonWidth = 40;
    private const int MaxButtonWidth = 72;

    private readonly RenderHelpers _helpers;
    private readonly IThemeProvider _themeProvider;

    public PodcastCtaRenderer(RenderHelpers helpers, IThemeProvider themeProvider)
    {
        _helpers = helpers;
        _themeProvider = themeProvider;
    }

    /// <summary>
    /// Renders the podcast CTA button at the current cursor position.
    /// Automatically selects the appropriate tier based on terminal dimensions.
    /// </summary>
    public void Render(RenderOptions options, PodcastCtaState state = PodcastCtaState.Idle)
    {
        var p = BuiltInThemes.Get(_themeProvider.CurrentTheme);
        var width = Math.Max(1, options.TerminalWidth - 2);
        var height = options.TerminalHeight;

        if (height < 20 || width < 35)
        {
            RenderInline(width, p, state);
        }
        else if (height < 24 || width < 50)
        {
            RenderCompactSlab(width, p, state);
        }
        else
        {
            RenderFullSlab(width, p, state);
        }
    }

    /// <summary>
    /// Gets the number of lines the CTA occupies at the given terminal dimensions.
    /// Used by layout calculations to reserve space.
    /// </summary>
    public static int GetCtaLineCount(int terminalWidth, int terminalHeight)
    {
        if (terminalHeight < 20 || terminalWidth - 2 < 35)
        {
            return 1;
        }

        if (terminalHeight < 24 || terminalWidth - 2 < 50)
        {
            return 3;
        }

        return 5;
    }

    /// <summary>
    /// Full Slab: 5 lines — blank, top fill, content line, bottom fill, blank.
    /// </summary>
    private void RenderFullSlab(int width, ThemePalette p, PodcastCtaState state)
    {
        var buttonWidth = ComputeButtonWidth(width);
        var pad = Math.Max(0, (width - buttonWidth) / 2);
        var padStr = new string(' ', pad);

        var (bgAnsi, fgAnsi, hintAnsi, dimmed) = GetStateColors(p, state);

        // Line 1: blank
        _helpers.WriteLine();

        // Line 2: top fill
        _helpers.WriteLine($"{padStr}{bgAnsi}{new string(' ', buttonWidth)}{Reset}");

        // Line 3: content line
        RenderContentLine(padStr, buttonWidth, bgAnsi, fgAnsi, hintAnsi, dimmed, state);

        // Line 4: bottom fill
        _helpers.WriteLine($"{padStr}{bgAnsi}{new string(' ', buttonWidth)}{Reset}");

        // Line 5: blank
        _helpers.WriteLine();
    }

    /// <summary>
    /// Compact Slab: 3 lines — blank, content line, blank.
    /// </summary>
    private void RenderCompactSlab(int width, ThemePalette p, PodcastCtaState state)
    {
        var buttonWidth = ComputeButtonWidth(width);
        var pad = Math.Max(0, (width - buttonWidth) / 2);
        var padStr = new string(' ', pad);

        var (bgAnsi, fgAnsi, hintAnsi, dimmed) = GetStateColors(p, state);

        // Line 1: blank
        _helpers.WriteLine();

        // Line 2: content line
        RenderContentLine(padStr, buttonWidth, bgAnsi, fgAnsi, hintAnsi, dimmed, state);

        // Line 3: blank
        _helpers.WriteLine();
    }

    /// <summary>
    /// Inline: 1 line — centered content only.
    /// </summary>
    private void RenderInline(int width, ThemePalette p, PodcastCtaState state)
    {
        var (_, fgAnsi, hintAnsi, dimmed) = GetStateColors(p, state);

        var content = $" {PlayIcon}  {Label} ";
        var hint = KeyHint;
        var totalLen = content.Length + hint.Length;
        var pad = Math.Max(0, (width - totalLen) / 2);

        var sb = new StringBuilder();
        sb.Append(new string(' ', pad));

        if (dimmed)
        {
            sb.Append($"{Dim}{fgAnsi}{content}{hintAnsi}{hint}{Reset}");
        }
        else
        {
            sb.Append($"{fgAnsi}{Bold}{content}{Reset}{hintAnsi}{hint}{Reset}");
        }

        _helpers.WriteLine(sb.ToString());
    }

    private void RenderContentLine(
        string padStr,
        int buttonWidth,
        string bgAnsi,
        string fgAnsi,
        string hintAnsi,
        bool dimmed,
        PodcastCtaState state)
    {
        var iconLabel = $" {PlayIcon}  {Label} ";
        var hint = $" {KeyHint} ";
        var unconfiguredHint = " Setup required ";
        var trailingText = state == PodcastCtaState.Unconfigured ? unconfiguredHint : hint;
        var innerSpace = Math.Max(1, buttonWidth - iconLabel.Length - trailingText.Length);

        var sb = new StringBuilder();
        sb.Append(padStr);
        sb.Append(bgAnsi);

        if (dimmed)
        {
            sb.Append($"{Dim}{fgAnsi}{iconLabel}");
        }
        else
        {
            sb.Append($"{fgAnsi}{Bold}{iconLabel}{Reset}{bgAnsi}");
        }

        sb.Append(new string(' ', innerSpace));
        sb.Append($"{hintAnsi}{trailingText}{Reset}{bgAnsi}");

        sb.Append(Reset);
        _helpers.WriteLine(sb.ToString());
    }

    private static int ComputeButtonWidth(int terminalWidth)
    {
        return Math.Clamp(terminalWidth - 12, MinButtonWidth, MaxButtonWidth);
    }

    private static (string BgAnsi, string FgAnsi, string HintAnsi, bool Dimmed) GetStateColors(
        ThemePalette p,
        PodcastCtaState state)
    {
        return state switch
        {
            PodcastCtaState.Pressed => (
                p.SelectedItemBg.AnsiBg,
                p.SelectedItemFg.AnsiFg,
                p.SelectedItemFg.AnsiFg,
                false),
            PodcastCtaState.Disabled => (
                p.SelectedItemBg.AnsiBg,
                p.SecondaryText.AnsiFg,
                p.SecondaryText.AnsiFg,
                true),
            PodcastCtaState.Unconfigured => (
                p.SelectedItemBg.AnsiBg,
                p.SecondaryText.AnsiFg,
                p.SecondaryText.AnsiFg,
                true),
            _ => (
                p.SelectedItemBg.AnsiBg,
                p.SelectedItemFg.AnsiFg,
                p.PrimaryText.AnsiFg,
                false),
        };
    }
}
