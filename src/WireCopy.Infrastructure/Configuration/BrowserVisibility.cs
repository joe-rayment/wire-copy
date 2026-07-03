// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Infrastructure.Configuration;

/// <summary>
/// LEGACY settings-binding enum (workspace-8cf2, retired by workspace-8ne3/9k27.10).
/// The browser is ALWAYS headful — there is no visibility policy to resolve
/// anymore. This type exists only so old settings files with a
/// <c>Browser.Visibility</c> value still deserialize; every value is ignored
/// (an explicit <see cref="Headless"/> is called out as IGNORED in
/// <c>BrowserConfiguration.DescribeVisibilityResolution</c>). On a display-less
/// host the app runs under a virtual display (Xvfb via the run script) — it
/// never degrades to headless.
/// </summary>
public enum BrowserVisibility
{
    /// <summary>Legacy value; ignored — the browser is always headful.</summary>
    Auto,

    /// <summary>Legacy value; ignored — the browser is always headful.</summary>
    Visible,

    /// <summary>Legacy value; IGNORED and flagged at startup — the browser is always headful.</summary>
    Headless,
}
