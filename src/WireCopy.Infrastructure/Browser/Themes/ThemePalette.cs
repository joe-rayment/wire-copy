// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Domain.ValueObjects.Browser;

namespace WireCopy.Infrastructure.Browser.Themes;

/// <summary>
/// Defines all color roles used across the terminal browser UI.
/// Each theme provides a complete palette of these roles.
/// </summary>
public record ThemePalette
{
    public required ThemeColor PrimaryText { get; init; }

    public required ThemeColor SecondaryText { get; init; }

    public required ThemeColor LinkContent { get; init; }

    public required ThemeColor LinkNavigation { get; init; }

    public required ThemeColor LinkExternal { get; init; }

    public required ThemeColor LinkFooter { get; init; }

    public required ThemeColor HeaderBorderFg { get; init; }

    public required ThemeColor HeaderTitleFg { get; init; }

    public required ThemeColor HeaderUrlFg { get; init; }

    public required ThemeColor SelectedItemFg { get; init; }

    public required ThemeColor SelectedItemBg { get; init; }

    public required ThemeColor StatusBarSeparatorFg { get; init; }

    public required ThemeColor StatusBarTextFg { get; init; }

    public required ThemeColor PromptFg { get; init; }

    public required ThemeColor PromptLabelFg { get; init; }

    public required ThemeColor ErrorFg { get; init; }

    public required ThemeColor SearchHighlightFg { get; init; }

    public required ThemeColor SearchHighlightBg { get; init; }

    public required ThemeColor ReadItemFg { get; init; }

    public required ThemeColor FocusIndicatorFg { get; init; }

    /// <summary>
    /// Accent color for interactive elements (links in hints, key shortcuts).
    /// Defaults to PromptFg if not explicitly set.
    /// </summary>
    public ThemeColor? AccentFg { get; init; }

    /// <summary>
    /// Very dim text for truly background elements (version info, decorative chars).
    /// Defaults to SecondaryText if not explicitly set.
    /// </summary>
    public ThemeColor? DimFg { get; init; }

    /// <summary>
    /// Quiet text for scroll hints, version numbers, de-emphasized labels.
    /// Defaults to SecondaryText if not explicitly set.
    /// </summary>
    public ThemeColor? MutedFg { get; init; }

    /// <summary>
    /// Success/completion color for checkmarks and confirmation moments.
    /// Defaults to PrimaryText if not explicitly set.
    /// </summary>
    public ThemeColor? SuccessFg { get; init; }

    /// <summary>
    /// Celebration color for rare earned moments (podcast complete, achievements).
    /// Defaults to HeaderTitleFg if not explicitly set.
    /// </summary>
    public ThemeColor? CelebrationFg { get; init; }

    /// <summary>
    /// Warning color for active progress, bot challenges, attention-needed states.
    /// Defaults to ErrorFg if not explicitly set.
    /// </summary>
    public ThemeColor? WarningFg { get; init; }

    /// <summary>
    /// Reader cursor line color (distinct from paragraph highlight).
    /// Defaults to SelectedItemFg if not explicitly set.
    /// </summary>
    public ThemeColor? ReaderCursorFg { get; init; }

    /// <summary>
    /// Gets the accent color, falling back to PromptFg.
    /// </summary>
    public ThemeColor GetAccentFg() => AccentFg ?? PromptFg;

    /// <summary>
    /// Gets the dim color, falling back to SecondaryText.
    /// </summary>
    public ThemeColor GetDimFg() => DimFg ?? SecondaryText;

    /// <summary>
    /// Gets the muted color, falling back to SecondaryText.
    /// </summary>
    public ThemeColor GetMutedFg() => MutedFg ?? SecondaryText;

    /// <summary>
    /// Gets the success color, falling back to PrimaryText.
    /// </summary>
    public ThemeColor GetSuccessFg() => SuccessFg ?? PrimaryText;

    /// <summary>
    /// Gets the celebration color, falling back to HeaderTitleFg.
    /// </summary>
    public ThemeColor GetCelebrationFg() => CelebrationFg ?? HeaderTitleFg;

    /// <summary>
    /// Gets the warning color, falling back to ErrorFg.
    /// </summary>
    public ThemeColor GetWarningFg() => WarningFg ?? ErrorFg;

    /// <summary>
    /// Gets the reader cursor color, falling back to SelectedItemFg.
    /// </summary>
    public ThemeColor GetReaderCursorFg() => ReaderCursorFg ?? SelectedItemFg;
}
