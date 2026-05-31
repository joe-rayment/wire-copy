// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Domain.Enums.Scheduling;

/// <summary>
/// workspace-frpl.2 — outcome of a recipe's most recent scheduled (or run-now)
/// occurrence. <see cref="Never"/> is the initial state before any run.
/// </summary>
public enum RunStatus
{
    Never,
    Success,
    PartialSuccess,
    Failed,
    Skipped,
}
