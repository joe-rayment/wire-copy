// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Infrastructure.BrowserExtension;

/// <summary>What <see cref="ReadingListCapture.SaveAsync"/> did with a page.</summary>
/// <param name="Title">The title the Reading List row was saved under.</param>
/// <param name="Words">Word count of the extracted article, or 0 when none was extracted.</param>
/// <param name="Source">"site-layout", "readability" or "none" (see <see cref="Browser.ArticleHtmlExtraction"/>).</param>
/// <param name="AlreadySaved">True when the URL was already on the Reading List (it is moved to the top).</param>
internal sealed record ReadingListCaptureResult(string Title, int Words, string Source, bool AlreadySaved);
