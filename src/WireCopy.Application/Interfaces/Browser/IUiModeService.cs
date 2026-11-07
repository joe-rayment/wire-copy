// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Application.Interfaces.Browser;

/// <summary>The app-wide colour mode (S5): normal, or routine colours.</summary>
public enum UiMode
{
    Normal,
    Routine,
}

/// <summary>Holds the current <see cref="UiMode"/>; whoever turns Routine on turns it off.</summary>
public interface IUiModeService
{
    UiMode Current { get; }

    void Set(UiMode mode);

    event Action? Changed;
}
