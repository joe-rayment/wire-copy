// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Infrastructure.Browser;

/// <summary>
/// workspace-romy.3 — one Set-of-Marks badge: the AI analyzer's link index and
/// the absolute URL used to find the anchor on the live page. Numbered badges
/// drawn on the page before the wizard screenshot give the model a visual
/// grounding between screenshot pixels and the numbered link list (Set-of-Mark
/// prompting, arXiv 2310.11441).
/// </summary>
public sealed record ScreenshotMark(int Index, string Url);
