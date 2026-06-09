// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Infrastructure.Browser;

/// <summary>
/// The selected story's identity in the TUI link tree, expressed in terms the
/// live page understands: which page it lives on, the anchor's absolute href,
/// and the display text used to disambiguate duplicate hrefs.
/// </summary>
public readonly record struct SpotlightTarget(string PageUrl, string LinkUrl, string DisplayText);
