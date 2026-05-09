// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Domain.Enums.Browser;

namespace WireCopy.Application.DTOs.Browser;

/// <summary>
/// Typed signal describing a human-in-the-loop action the user must perform
/// in the browser before the page can be loaded successfully (CAPTCHA solve,
/// login, cookie-consent acceptance, 2FA code, paywall walk-through, etc.).
///
/// <para>
/// Replaces the previous untyped <see cref="PageLoadResult.ErrorMessage"/>
/// strings ("Bot challenge could not be resolved", "HTTP 403"…) that were
/// indistinguishable to UI consumers. Renderers and the status bar dispatch
/// off <see cref="Variant"/> to produce variant-specific copy.
/// </para>
/// </summary>
/// <param name="Variant">Kind of action required (CAPTCHA, login, cookie-consent, etc.).</param>
/// <param name="Domain">The domain the action is required on, for use in copy ("Log in at <c>nytimes.com</c>").</param>
/// <param name="Detail">Optional free-form detail — e.g., redirected interstitial path, HTTP status code text.</param>
public sealed record HumanActionRequired(
    HumanActionVariant Variant,
    string Domain,
    string? Detail = null);
