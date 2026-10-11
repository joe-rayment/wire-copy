// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Domain.Entities.Browser;
using WireCopy.Domain.Enums.Browser;

namespace WireCopy.Infrastructure.Browser;

/// <summary>What <see cref="ArticleHtmlExtraction.ExtractAsync"/> found in a page.</summary>
/// <param name="Classification">The page kind.</param>
/// <param name="Readable">The article content, or null when there is none.</param>
/// <param name="Source">"site-layout", "readability" or "none".</param>
internal sealed record ArticleExtractionResult(
    PageClassification Classification,
    ReadableContent? Readable,
    string Source);
