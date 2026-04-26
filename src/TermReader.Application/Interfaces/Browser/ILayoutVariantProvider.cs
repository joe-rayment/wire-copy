// Licensed under the MIT License. See LICENSE in the repository root.

using TermReader.Domain.Enums.Browser;

namespace TermReader.Application.Interfaces.Browser;

/// <summary>
/// Provides layout variant cycling and persistence for each ViewMode.
/// Each ViewMode has a set of named layout variants that the user can cycle through.
/// </summary>
public interface ILayoutVariantProvider
{
    /// <summary>
    /// Gets the current variant name for the given view mode.
    /// </summary>
    string GetCurrentVariant(ViewMode mode);

    /// <summary>
    /// Cycles to the next variant for the given view mode and returns the new variant name.
    /// Wraps around to the first variant after the last.
    /// </summary>
    string CycleVariant(ViewMode mode);

    /// <summary>
    /// Gets all available variant names for the given view mode.
    /// </summary>
    IReadOnlyList<string> GetAvailableVariants(ViewMode mode);

    /// <summary>
    /// Gets the zero-based index of the current variant for the given view mode.
    /// </summary>
    int GetCurrentIndex(ViewMode mode);

    /// <summary>
    /// Gets the total number of variants available for the given view mode.
    /// </summary>
    int GetTotalVariants(ViewMode mode);
}
