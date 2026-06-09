// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Infrastructure.Browser;

/// <summary>
/// The selected story's identity in the TUI link tree, expressed in terms the
/// live page understands: which page it lives on, the anchor's absolute href,
/// and the display text used to disambiguate duplicate hrefs.
///
/// <para>
/// When <paramref name="FollowPageOnly"/> is true (reader view, workspace-nqqs) there is
/// no anchor to light up — the page itself is the content — so the spotlight only keeps
/// the live window navigated to <paramref name="PageUrl"/> and draws no highlight box.
/// </para>
/// </summary>
public readonly record struct SpotlightTarget(string PageUrl, string LinkUrl, string DisplayText, bool FollowPageOnly = false);
