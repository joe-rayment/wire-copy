// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Infrastructure.Configuration;

/// <summary>
/// The ONE browser-visibility policy (workspace-8cf2): headed is the rule,
/// headless the exception. Resolved once per process and logged at startup so
/// "why is this headless" is never a mystery.
/// </summary>
public enum BrowserVisibility
{
    /// <summary>
    /// Resolve automatically: VISIBLE when the session is interactive (a real
    /// TTY) and a display exists; headless for unattended runs (cron/scheduled,
    /// redirected stdio) and display-less hosts (CI, SSH).
    /// </summary>
    Auto,

    /// <summary>Always headed. Falls back to headless only if no display exists (launch-time fallback).</summary>
    Visible,

    /// <summary>Always headless (explicit opt-out of the visible-first rule).</summary>
    Headless,
}
