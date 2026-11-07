// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Infrastructure.Browser.CommandHandlers;

/// <summary>
/// One line of the routine editor. <see cref="SiteUrl"/> is set on Site and Section rows (the first
/// step's SourceUrl of that site); <see cref="StepIndex"/> is the index into <c>recipe.Steps</c> on
/// Section rows only.
/// </summary>
internal sealed record RoutineRow(
    RoutineRowKind Kind,
    string Label,
    string Value = "",
    string? SiteUrl = null,
    int StepIndex = -1)
{
    public bool Selectable => Kind is not (RoutineRowKind.ContentsHeading or RoutineRowKind.NothingYet);
}
