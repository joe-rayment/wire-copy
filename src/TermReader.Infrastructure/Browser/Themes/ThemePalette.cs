// Educational and personal use only.

using TermReader.Domain.ValueObjects.Browser;

namespace TermReader.Infrastructure.Browser.Themes;

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
}
