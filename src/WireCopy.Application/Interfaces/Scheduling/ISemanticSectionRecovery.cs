// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Application.DTOs.Scheduling;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Domain.ValueObjects.Scheduling;

namespace WireCopy.Application.Interfaces.Scheduling;

/// <summary>
/// workspace-frpl.10 (B9b) — the LAST-resort run-time recovery tier for a scheduled
/// step, sitting between B9a's deterministic selector re-derivation and the loud
/// skip. When the durable section matched 0 today and re-derivation also failed, it
/// spends ONE budgeted model round-trip to semantically classify which heading on
/// the page today corresponds to the wanted section, auto-accepts ONLY a single
/// high-confidence candidate that ALSO passes a self-test, and returns a
/// <see cref="ResolutionStatus.Recovered"/> resolution (run-LOCAL — never written
/// back to the shared config). Returns null to fall through to the loud skip when
/// the model declines, is low-confidence/ambiguous, the self-test fails, the model
/// is not configured, or the per-day call budget is exhausted.
/// </summary>
public interface ISemanticSectionRecovery
{
    Task<SectionResolution?> TryRecoverAsync(
        SiteHierarchyConfig config,
        IReadOnlyList<LinkInfo> todaysLinks,
        RecipeStep step,
        CancellationToken cancellationToken = default);
}
