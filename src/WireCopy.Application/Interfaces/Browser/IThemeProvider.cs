// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Domain.Enums.Browser;

namespace WireCopy.Application.Interfaces.Browser;

/// <summary>
/// Provides theme management for the terminal browser UI.
/// </summary>
public interface IThemeProvider
{
    /// <summary>
    /// Gets the currently active theme.
    /// </summary>
    ThemeName CurrentTheme { get; }

    /// <summary>
    /// Cycles to the next theme in the rotation and returns its name.
    /// </summary>
    ThemeName CycleTheme();

    /// <summary>
    /// Sets the active theme to the specified value.
    /// </summary>
    void SetTheme(ThemeName theme);
}
