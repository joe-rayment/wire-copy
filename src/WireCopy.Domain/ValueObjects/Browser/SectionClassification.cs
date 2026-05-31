// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Domain.ValueObjects.Browser;

/// <summary>
/// workspace-frpl.10 (B9b) — the result of a single-label semantic classification:
/// given the section headings present on a page today and the durable intent of a
/// recipe step, the model picks the ONE candidate heading that best matches the
/// intent, with a 0..1 confidence. <see cref="CandidateLabel"/> is null when the
/// model declines to map the intent to any present heading. Used only as the
/// last-resort recovery tier (after deterministic re-derivation) and only when a
/// SINGLE high-confidence candidate also passes a self-test, so a low-confidence
/// or ambiguous answer deterministically falls through to a loud skip.
/// </summary>
public sealed record SectionClassification
{
    public string? CandidateLabel { get; init; }

    public double Confidence { get; init; }

    public static SectionClassification None { get; } = new();
}
