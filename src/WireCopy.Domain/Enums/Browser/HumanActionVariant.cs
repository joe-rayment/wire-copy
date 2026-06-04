// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Domain.Enums.Browser;

/// <summary>
/// Variant of a human-in-the-loop action required to unblock a page load.
/// Drives the variant-specific copy in <see cref="WireCopy.Application.DTOs.Browser.HumanActionRequired"/>
/// and the matching reader-view box in <see cref="WireCopy.Application.Interfaces.Browser.IPageRenderer"/>.
///
/// <para>
/// When detector confidence is low or signals are ambiguous, callers should
/// fall back to <see cref="Generic"/> rather than guessing a specific variant —
/// misidentified copy ("solve the captcha" when it's really a cookie banner)
/// is worse than generic actionable copy (workspace-0b9s robustness review).
/// </para>
/// </summary>
public enum HumanActionVariant
{
    /// <summary>
    /// Site is showing a CAPTCHA / bot-challenge interstitial (DataDome, Cloudflare, hCaptcha, reCAPTCHA).
    /// </summary>
    Captcha,

    /// <summary>
    /// Site requires the user to log in (HTTP 401/403 or login form heuristics on a paywalled domain).
    /// </summary>
    Login,

    /// <summary>
    /// Cookie consent banner (GDPR, OneTrust, Quantcast Choice) blocking content.
    /// </summary>
    CookieConsent,

    /// <summary>
    /// Two-factor authentication / one-time code required.
    /// </summary>
    TwoFactor,

    /// <summary>
    /// Article is paywalled — distinct from a login wall in that the user may have an account
    /// but no subscription, or no account at all.
    /// </summary>
    Paywall,

    /// <summary>
    /// Site blocks this region (HTTP 451 or geo-restriction text).
    /// </summary>
    RegionBlock,

    /// <summary>
    /// Site is stuck in a redirect loop — the browser hit
    /// <c>net::ERR_TOO_MANY_REDIRECTS</c> or the HTTP client exhausted its
    /// automatic-redirect budget and returned a final 3xx. Almost always a
    /// cookie/consent or bot-management (Cloudflare) bounce that never settles
    /// in our session (workspace-odn5: macleans.ca). Surfaced so the user gets
    /// an actionable "open in your browser, then press R" box instead of an
    /// opaque <c>net::ERR_TOO_MANY_REDIRECTS</c> / <c>HTTP 302</c> string.
    /// </summary>
    RedirectLoop,

    /// <summary>
    /// Generic fallback when the detector saw HITL-like signals but could not classify them
    /// with high confidence. Triggers low-specificity copy ("Something on {domain} needs
    /// your attention").
    /// </summary>
    Generic,
}
