// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;

namespace WireCopy.Infrastructure.Browser;

/// <summary>A page's classification with the signals that produced it (for the caller's logging).</summary>
/// <param name="Classification">The page kind.</param>
/// <param name="Score">The raw classification score.</param>
/// <param name="Signals">The structural signals extracted from the HTML.</param>
/// <param name="ContentLinkCount">How many extracted links were content links.</param>
internal sealed record PageClassificationResult(
    PageClassification Classification,
    int Score,
    PageSignals Signals,
    int ContentLinkCount);
