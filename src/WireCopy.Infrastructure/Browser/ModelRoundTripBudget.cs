// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Infrastructure.Browser;

/// <summary>
/// workspace-5oe9.8 — a hard ceiling on the number of model round-trips a single
/// AI-setup interaction may spend, shared across the wizard (B8a), the
/// point-at-a-link re-inference (B8b), and the degenerate-retry (B13). The base
/// contract (B7) is two calls (propose + infer); the default ceiling of 4
/// allows at most one override re-inference and one degenerate retry on top.
/// Enforced in code (not just documented) so no path can spin the model
/// unbounded.
/// workspace-romy.7: default raised 4 -> 8. The old ceiling left exactly ONE
/// adjustment after propose + infer + auto-repair, so the wizard's
/// adjust-and-re-preview loop dead-ended after a single round. 8 funds the
/// base contract plus several genuine refinement rounds while still bounding
/// cost; the adjust card surfaces the remaining calls when they run low.
/// </summary>
internal sealed class ModelRoundTripBudget
{
    public ModelRoundTripBudget(int max = 8)
    {
        Max = max;
    }

    public int Max { get; }

    public int Used { get; private set; }

    public bool Exhausted => Used >= Max;

    /// <summary>Round-trips still available (never negative).</summary>
    public int Remaining => Math.Max(0, Max - Used);

    /// <summary>
    /// Reserves one round-trip if budget remains. Returns false when the ceiling
    /// is hit — callers must then skip the model call and degrade gracefully.
    /// </summary>
    public bool TrySpend()
    {
        if (Used >= Max)
        {
            return false;
        }

        Used++;
        return true;
    }
}
