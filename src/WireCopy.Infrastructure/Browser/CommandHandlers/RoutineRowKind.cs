// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Infrastructure.Browser.CommandHandlers;

/// <summary>What a routine-editor row is; decides what Enter, x and r do on it.</summary>
internal enum RoutineRowKind
{
    Name,
    Frequency,
    Podcast,
    Enabled,
    ContentsHeading,
    Site,
    Section,
    NothingYet,
    AddSite,
}
