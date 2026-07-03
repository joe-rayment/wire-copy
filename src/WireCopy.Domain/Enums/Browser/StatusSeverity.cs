// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Domain.Enums.Browser;

/// <summary>
/// workspace-9k27.11: priority of a status message in the Transient slot.
/// Errors always take the slot immediately (stomping any keyed teach hint);
/// Info messages defer behind an active keyed hint or error and surface when
/// its TTL lapses — nothing user-initiated vanishes silently.
/// </summary>
public enum StatusSeverity
{
    /// <summary>Informational feedback; defers behind keyed hints and errors.</summary>
    Info,

    /// <summary>A failed user action; shown immediately, over any keyed hint.</summary>
    Error,
}
