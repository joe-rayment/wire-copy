// Licensed under the MIT License. See LICENSE in the repository root.

using System.Text;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Infrastructure.Browser.Themes;
using WireCopy.Infrastructure.Browser.UI.Components;

namespace WireCopy.Infrastructure.Browser.UI.Renderers;

/// <summary>
/// Renders the podcast call-to-action button in collection views.
/// Three tiers: Hero (7 lines, bordered box), Compact Slab (3 lines), Inline (1 line).
/// </summary>
internal class PodcastCtaRenderer
{
    private const string Reset = "\x1b[0m";
    private const string Bold = "\x1b[1m";
    private const string Dim = "\x1b[2m";
    private const string HeroPlayIcon = "\u25b6\u25b6\u25b6";
    private const string PlayIcon = "\u25b6\u25b6";
    private const string HeroLabel = "GENERATE PODCAST";
    private const string Label = "Generate Podcast";
    private const string HeroSubtitle = "turn your reading list into audio";
    private const string GeneratingLabel = "GENERATING...";
    private const string KeyHint = "[p]";
    private const string SelectedKeyHint = "[Enter]";
    private const int MinButtonWidth = 40;
    private const int MaxButtonWidth = 72;
    private const double MinutesPerArticle = 3.5;

    /// <summary>
    /// Minimum terminal height to render the hero box CTA (reserves 7 lines for the
    /// CTA and still leaves room for the box header, status bar, and a couple items).
    /// </summary>
    private const int HeroHeightThreshold = 22;

    /// <summary>
    /// Minimum terminal height to render the compact slab CTA (3 lines).
    /// </summary>
    private const int CompactHeightThreshold = 18;

    private readonly RenderHelpers _helpers;
    private readonly IThemeProvider _themeProvider;

    public PodcastCtaRenderer(RenderHelpers helpers, IThemeProvider themeProvider)
    {
        _helpers = helpers;
        _themeProvider = themeProvider;
    }

    /// <summary>
    /// Gets the number of lines the CTA occupies at the given terminal dimensions.
    /// Used by layout calculations to reserve space.
    /// </summary>
    public static int GetCtaLineCount(int terminalWidth, int terminalHeight)
    {
        if (terminalHeight >= HeroHeightThreshold && terminalWidth - 2 >= 50)
        {
            return 7;
        }

        if (terminalHeight >= CompactHeightThreshold && terminalWidth - 2 >= 35)
        {
            return 3;
        }

        return 1;
    }

    /// <summary>
    /// Renders the podcast CTA button at the current cursor position.
    /// Automatically selects the appropriate tier based on terminal dimensions.
    /// </summary>
    public void Render(RenderOptions options, PodcastCtaState state = PodcastCtaState.Idle, int articleCount = 0)
    {
        var p = BuiltInThemes.Get(_themeProvider.CurrentTheme);
        var width = Math.Max(1, options.TerminalWidth - 2);
        var height = options.TerminalHeight;
        var progressFraction = options.PodcastProgressFraction;

        if (state == PodcastCtaState.Generating)
        {
            if (height >= HeroHeightThreshold && width >= 50)
            {
                RenderGeneratingHeroBox(width, p, progressFraction, articleCount);
            }
            else if (height >= CompactHeightThreshold && width >= 35)
            {
                RenderGeneratingCompactSlab(width, p, progressFraction);
            }
            else
            {
                RenderGeneratingInline(width, p, progressFraction);
            }

            return;
        }

        if (height >= HeroHeightThreshold && width >= 50)
        {
            RenderHeroBox(width, p, state, articleCount);
        }
        else if (height >= CompactHeightThreshold && width >= 35)
        {
            RenderCompactSlab(width, p, state);
        }
        else
        {
            RenderInline(width, p, state);
        }
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
            PodcastCtaState.Selected => (
                p.SelectedItemFg.AnsiBg,
                p.SelectedItemBg.AnsiFg,
                p.SelectedItemBg.AnsiFg,
                false),
            PodcastCtaState.Generating => (
                p.SelectedItemBg.AnsiBg,
                p.GetCelebrationFg().AnsiFg,
                p.GetCelebrationFg().AnsiFg,
                false),
            _ => (
                p.SelectedItemBg.AnsiBg,
                p.SelectedItemFg.AnsiFg,
                p.PrimaryText.AnsiFg,
                false),
        };
    }

    /// <summary>
    /// Computes the inner width and centering padding for the hero bordered box.
    /// </summary>
    private static (int InnerWidth, string PadStr) ComputeHeroBox(int terminalWidth)
    {
        var boxWidth = Math.Clamp(terminalWidth - 8, MinButtonWidth, MaxButtonWidth);
        var pad = Math.Max(0, (terminalWidth - boxWidth) / 2);
        var innerWidth = Math.Max(1, boxWidth - 2);
        return (innerWidth, new string(' ', pad));
    }

    /// <summary>
    /// Hero Box: 7 lines -- pink-bordered box with title, subtitle, and metadata.
    /// Layout:
    /// ╭──────────────────────────────────────────────╮
    /// │                                              │
    /// │       ▶▶▶  GENERATE PODCAST                 │
    /// │       turn your reading list into audio      │
    /// │                                              │
    /// │       5 articles · ~18 min          [p]      │
    /// ╰──────────────────────────────────────────────╯
    /// </summary>
    private void RenderHeroBox(int width, ThemePalette p, PodcastCtaState state, int articleCount)
    {
        var (innerWidth, padStr) = ComputeHeroBox(width);
        var borderFg = p.GetCelebrationFg().AnsiFg;
        var titleFg = p.GetCelebrationFg().AnsiFg;
        var subtitleFg = p.GetSuccessFg().AnsiFg;
        var metaFg = p.GetSuccessFg().AnsiFg;
        var accentFg = p.GetAccentFg().AnsiFg;
        var contentIndent = 6;

        // Build metadata text
        var estMinutes = Math.Max(1, (int)Math.Round(articleCount * MinutesPerArticle));
        var articleText = articleCount == 1 ? "1 article" : $"{articleCount} articles";
        var metaText = $"{articleText} \u00b7 ~{estMinutes} min";

        // Line 1: top border  ╭────╮
        _helpers.WriteLine($"{padStr}{borderFg}\u256d{new string('\u2500', innerWidth)}\u256e{Reset}");

        // Line 2: blank row  │    │
        _helpers.WriteLine($"{padStr}{borderFg}\u2502{Reset}{new string(' ', innerWidth)}{borderFg}\u2502{Reset}");

        // Line 3: ▶▶▶  GENERATE PODCAST
        var titleContent = $"{HeroPlayIcon}  {HeroLabel}";
        var titlePad = Math.Max(0, innerWidth - contentIndent - titleContent.Length);
        _helpers.WriteLine(
            $"{padStr}{borderFg}\u2502{Reset}" +
            $"{new string(' ', contentIndent)}" +
            $"{titleFg}{Bold}{titleContent}{Reset}" +
            $"{new string(' ', titlePad)}" +
            $"{borderFg}\u2502{Reset}");

        // Line 4: subtitle
        var subPad = Math.Max(0, innerWidth - contentIndent - HeroSubtitle.Length);
        _helpers.WriteLine(
            $"{padStr}{borderFg}\u2502{Reset}" +
            $"{new string(' ', contentIndent)}" +
            $"{subtitleFg}{HeroSubtitle}{Reset}" +
            $"{new string(' ', subPad)}" +
            $"{borderFg}\u2502{Reset}");

        // Line 5: blank row  │    │
        _helpers.WriteLine($"{padStr}{borderFg}\u2502{Reset}{new string(' ', innerWidth)}{borderFg}\u2502{Reset}");

        // Line 6: metadata + key hint
        var activeHint = state == PodcastCtaState.Selected ? SelectedKeyHint : KeyHint;
        var metaAndHint = metaText.Length + activeHint.Length;
        var metaGap = Math.Max(2, innerWidth - contentIndent - metaAndHint);
        _helpers.WriteLine(
            $"{padStr}{borderFg}\u2502{Reset}" +
            $"{new string(' ', contentIndent)}" +
            $"{metaFg}{metaText}{Reset}" +
            $"{new string(' ', metaGap)}" +
            $"{accentFg}{activeHint}{Reset}" +
            $"{borderFg}\u2502{Reset}");

        // Line 7: bottom border  ╰────╯
        _helpers.WriteLine($"{padStr}{borderFg}\u2570{new string('\u2500', innerWidth)}\u256f{Reset}");
    }

    /// <summary>
    /// Generating state in hero box: shows progress bar instead of metadata.
    /// </summary>
    private void RenderGeneratingHeroBox(int width, ThemePalette p, double progressFraction, int articleCount)
    {
        var (innerWidth, padStr) = ComputeHeroBox(width);
        var borderFg = p.GetCelebrationFg().AnsiFg;
        var titleFg = p.GetCelebrationFg().AnsiFg;
        var subtitleFg = p.GetSuccessFg().AnsiFg;
        var contentIndent = 6;

        var percent = (int)(Math.Clamp(progressFraction, 0.0, 1.0) * 100);
        var pctText = $" {percent}%";

        var articleText = articleCount == 1 ? "1 article" : $"{articleCount} articles";
        var subText = $"mixing {articleText} into a single episode";

        // Progress bar width: inner width minus indent, pctText, and small padding
        var barWidth = Math.Max(4, innerWidth - contentIndent - pctText.Length - 1);
        var filledColor = percent >= 100 ? p.GetSuccessFg().AnsiFg : p.GetCelebrationFg().AnsiFg;
        var bar = Indicators.RenderEighthBlockBar(filledColor, p.GetMutedFg().AnsiFg, progressFraction, barWidth);

        // Line 1: top border
        _helpers.WriteLine($"{padStr}{borderFg}\u256d{new string('\u2500', innerWidth)}\u256e{Reset}");

        // Line 2: blank
        _helpers.WriteLine($"{padStr}{borderFg}\u2502{Reset}{new string(' ', innerWidth)}{borderFg}\u2502{Reset}");

        // Line 3: ▶▶▶  GENERATING...
        var genTitle = $"{HeroPlayIcon}  {GeneratingLabel}";
        var genTitlePad = Math.Max(0, innerWidth - contentIndent - genTitle.Length);
        _helpers.WriteLine(
            $"{padStr}{borderFg}\u2502{Reset}" +
            $"{new string(' ', contentIndent)}" +
            $"{titleFg}{Bold}{genTitle}{Reset}" +
            $"{new string(' ', genTitlePad)}" +
            $"{borderFg}\u2502{Reset}");

        // Line 4: subtitle
        var subPad = Math.Max(0, innerWidth - contentIndent - subText.Length);
        _helpers.WriteLine(
            $"{padStr}{borderFg}\u2502{Reset}" +
            $"{new string(' ', contentIndent)}" +
            $"{subtitleFg}{subText}{Reset}" +
            $"{new string(' ', subPad)}" +
            $"{borderFg}\u2502{Reset}");

        // Line 5: blank
        _helpers.WriteLine($"{padStr}{borderFg}\u2502{Reset}{new string(' ', innerWidth)}{borderFg}\u2502{Reset}");

        // Line 6: progress bar + percentage
        // bar is already ANSI-colored; we need to pad to fill innerWidth
        var barDisplayWidth = barWidth + pctText.Length;
        var barPad = Math.Max(0, innerWidth - contentIndent - barDisplayWidth);
        _helpers.WriteLine(
            $"{padStr}{borderFg}\u2502{Reset}" +
            $"{new string(' ', contentIndent)}" +
            $"{bar}" +
            $"{titleFg}{Bold}{pctText}{Reset}" +
            $"{new string(' ', barPad)}" +
            $"{borderFg}\u2502{Reset}");

        // Line 7: bottom border
        _helpers.WriteLine($"{padStr}{borderFg}\u2570{new string('\u2500', innerWidth)}\u256f{Reset}");
    }

    /// <summary>
    /// Compact Slab: 3 lines -- blank, content line, blank.
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
    /// Compact Slab Generating: 3 lines -- blank, title+progress, blank.
    /// </summary>
    private void RenderGeneratingCompactSlab(int width, ThemePalette p, double fraction)
    {
        var buttonWidth = ComputeButtonWidth(width);
        var pad = Math.Max(0, (width - buttonWidth) / 2);
        var padStr = new string(' ', pad);

        var bgAnsi = p.SelectedItemBg.AnsiBg;
        var titleColor = p.GetCelebrationFg().AnsiFg;

        // Line 1: blank
        _helpers.WriteLine();

        // Line 2: title + progress bar
        RenderGeneratingContentLine(padStr, buttonWidth, bgAnsi, titleColor, p, fraction);

        // Line 3: blank
        _helpers.WriteLine();
    }

    /// <summary>
    /// Inline: 1 line -- centered content only.
    /// </summary>
    private void RenderInline(int width, ThemePalette p, PodcastCtaState state)
    {
        var (_, fgAnsi, hintAnsi, dimmed) = GetStateColors(p, state);

        var content = $" {PlayIcon}  {Label} ";
        var hint = state == PodcastCtaState.Selected ? SelectedKeyHint : KeyHint;
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

    /// <summary>
    /// Inline Generating: 1 line -- chevrons + GENERATING... + percentage.
    /// </summary>
    private void RenderGeneratingInline(int width, ThemePalette p, double fraction)
    {
        var titleColor = p.GetCelebrationFg().AnsiFg;
        var percent = (int)(Math.Clamp(fraction, 0.0, 1.0) * 100);

        var content = $" {HeroPlayIcon}  {GeneratingLabel} ";
        var pctText = $" {percent}%";
        var totalLen = content.Length + pctText.Length;
        var pad = Math.Max(0, (width - totalLen) / 2);

        var sb = new StringBuilder();
        sb.Append(new string(' ', pad));
        sb.Append($"{titleColor}{Bold}{content}{Reset}{titleColor}{pctText}{Reset}");

        _helpers.WriteLine(sb.ToString());
    }

    /// <summary>
    /// Renders the generating content line with chevrons, title, progress bar, and percentage.
    /// </summary>
    private void RenderGeneratingContentLine(
        string padStr,
        int buttonWidth,
        string bgAnsi,
        string titleColor,
        ThemePalette p,
        double fraction)
    {
        var percent = (int)(Math.Clamp(fraction, 0.0, 1.0) * 100);
        var iconLabel = $" {HeroPlayIcon}  {GeneratingLabel} ";
        var pctText = $" {percent}% ";

        // Progress bar fills remaining space between label and percentage
        var barWidth = Math.Max(4, buttonWidth - iconLabel.Length - pctText.Length - 1);
        var filledColor = percent >= 100 ? p.GetSuccessFg().AnsiFg : p.GetCelebrationFg().AnsiFg;
        var bar = Indicators.RenderEighthBlockBar(filledColor, p.GetMutedFg().AnsiFg, fraction, barWidth);

        var sb = new StringBuilder();
        sb.Append(padStr);
        sb.Append(bgAnsi);
        sb.Append($"{titleColor}{Bold}{iconLabel}{Reset}{bgAnsi}");
        sb.Append(bar);
        sb.Append($"{bgAnsi}{titleColor}{pctText}{Reset}{bgAnsi}");
        sb.Append(Reset);
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
        var activeHint = state == PodcastCtaState.Selected ? SelectedKeyHint : KeyHint;
        var hint = $" {activeHint} ";
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
}
