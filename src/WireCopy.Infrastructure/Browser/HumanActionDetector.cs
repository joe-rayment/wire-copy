// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Application.DTOs.Browser;
using WireCopy.Domain.Enums.Browser;

namespace WireCopy.Infrastructure.Browser;

/// <summary>
/// Pure-static detector that consolidates the previously scattered bot-challenge,
/// login-wall, cookie-consent, 2FA, paywall and region-block heuristics into one
/// entry point returning a typed <see cref="HumanActionRequired"/> verdict.
///
/// <para>
/// Replaces the three duplicated bot-string lists previously living in
/// <see cref="PageLoader.IsBotChallengePage"/>, <c>PageLoader.IsJavaScriptRequired</c>
/// and <see cref="Cache.BackgroundPreloadService.IsBotDetectionResponse"/> — those
/// methods are now thin wrappers that call into <see cref="Detect"/> for backwards
/// compatibility.
/// </para>
///
/// <para>
/// Returns <see langword="null"/> when nothing HITL-like is detected. Returns
/// <see cref="HumanActionVariant.Generic"/> as the safe fallback when signals are
/// ambiguous (per the workspace-0b9s robustness review: misidentified specific copy
/// is worse than generic actionable copy).
/// </para>
/// </summary>
public static class HumanActionDetector
{
    private const int RealPageThreshold = 20 * 1024;
    private const int SmallPageThreshold = 5 * 1024;

    // workspace-kq4b: split captcha indicators by confidence so large pages
    // with noise keywords (e.g. Cloudflare's "challenge-platform" injected on
    // every page of a CF-fronted site, even when no challenge is shown) no
    // longer trip a Generic "action needed" badge on healthy pages. Strong
    // indicators are vendor-confirmed markers that genuinely correlate with
    // a challenge; weak indicators are noisy keywords that must be paired
    // with a small page size before they imply an interruption.
    private static readonly string[] StrongCaptchaIndicators =
    {
        // DataDome
        "captcha-delivery.com",
        "geo.captcha-delivery.com",
        "datadome",

        // Cloudflare — vendor-specific challenge UI markers (NOT the
        // "challenge-platform" script which CF injects on healthy pages too)
        "cf-challenge",
        "cf-browser-verification",
        "attention required! | cloudflare",
        "checking your browser",
        "just a moment...",

        // hCaptcha / reCAPTCHA explicit markers
        "g-recaptcha",
        "h-captcha",
        "hcaptcha.com",
    };

    private static readonly string[] WeakCaptchaIndicators =
    {
        // Cloudflare's ongoing bot-monitor injects this on EVERY page of a
        // CF-fronted site (e.g. thestar.com) — only meaningful on a tiny page.
        "challenge-platform",

        // Generic keywords — too noisy to fire on a large article page that
        // happens to mention them.
        "you have been blocked",
        "access denied",
        "enable cookies",
    };

    // Combined accessor kept so the back-compat IsBotDetectionResponse path
    // still scans the full keyword set.
    private static readonly string[] CaptchaIndicators =
        StrongCaptchaIndicators.Concat(WeakCaptchaIndicators).ToArray();

    // workspace-3rtr: HARD bot / WAF blocks — access denied outright, NOT a solvable challenge.
    // Strong markers are unambiguous access-denied text that won't appear in a real article at any
    // size (Cloudflare's "Sorry, you have been blocked" headline, the numeric CF error codes, the
    // WAF "security service to protect itself" banner). These get a confident BotBlock verdict so the
    // user sees the real reason (and the "open in a real browser" remedy) instead of a wrong "captcha"
    // badge, and so the human-action watcher does not spin waiting on a challenge that doesn't exist.
    private static readonly string[] StrongBotBlockIndicators =
    {
        "sorry, you have been blocked",
        "this website is using a security service to protect itself",
        "error code: 1020",
        "error code: 1015",
        "error code: 1010",
        "error code: 1009",
        "error 1020",
        "error 1015",
    };

    // Weaker access-denied phrases — real enough on a SMALL 403 page, but too generic to fire on a
    // large body that merely uses the words. Only counted alongside an HTTP 403 on a non-large page.
    private static readonly string[] WeakBotBlockIndicators =
    {
        "you have been blocked",
        "access denied",
        "you don't have permission to access",
        "you are unable to access",
    };

    // Markers of a SOLVABLE challenge (interactive CAPTCHA / "checking your browser" interstitial).
    // When present, the page is a challenge the user can pass — defer to the captcha path rather than
    // calling it a hard BotBlock, even if access-denied-ish words also appear.
    private static readonly string[] SolvableChallengeIndicators =
    {
        "cf-challenge",
        "cf-browser-verification",
        "checking your browser",
        "just a moment...",
        "challenge-platform",
        "g-recaptcha",
        "h-captcha",
        "hcaptcha.com",
        "captcha-delivery.com",
        "datadome",
        "turnstile",
    };

    private static readonly string[] CookieConsentIndicators =
    {
        // OneTrust
        "id=\"onetrust-banner-sdk\"",
        "onetrust-banner-sdk",
        "onetrust-consent-sdk",

        // Quantcast Choice
        "qc-cmp2-container",
        "qc-cmp-ui-container",

        // Generic GDPR / consent
        "id=\"cookie-consent\"",
        "id=\"gdpr-consent\"",
        "class=\"cookie-banner",
        "class=\"gdpr-banner",
        "data-cookieconsent",
    };

    // workspace-lmwm: split 2FA indicators by kind, mirroring the kq4b captcha
    // split. Markup indicators are form attributes that only appear on real OTP
    // forms; prose indicators ("two-factor", "verification code", …) appear in
    // ordinary article text — an NYT article ABOUT two-factor auth tripped a
    // TwoFactor verdict — so they only count on small (interstitial-sized) pages.
    private static readonly string[] TwoFactorMarkupIndicators =
    {
        "autocomplete=\"one-time-code\"",
        "autocomplete='one-time-code'",
    };

    private static readonly string[] TwoFactorProseIndicators =
    {
        "verification code",
        "6-digit code",
        "two-factor",
        "two factor",
        "authenticator app",
    };

    private static readonly string[] RegionBlockTextIndicators =
    {
        "not available in your country",
        "not available in your region",
        "geo-restricted",
        "this content is unavailable in your location",
        "451 unavailable for legal reasons",
    };

    private static readonly string[] PaywallTextIndicators =
    {
        "subscribe to continue",
        "subscribe to read",
        "subscribe to keep reading",
        "this article is for subscribers",
        "to continue reading, subscribe",
        "create a free account to read",
    };

    private static readonly string[] LoginFormIndicators =
    {
        "input type=\"password\"",
        "input type='password'",
        "id=\"signin\"",
        "id=\"login\"",
        "name=\"password\"",
        "form action=\"/login",
        "form action=\"/signin",
        "form action=\"/auth",
    };

    /// <summary>
    /// Inspects the response and returns the matching <see cref="HumanActionRequired"/>,
    /// or <see langword="null"/> when no HITL action is detected.
    /// </summary>
    /// <param name="html">Raw HTML body (may be empty when status code is the only signal).</param>
    /// <param name="url">Final URL after redirects — used to derive the domain in the verdict.</param>
    /// <param name="statusCode">HTTP status code (or <c>0</c> when not applicable).</param>
    public static HumanActionRequired? Detect(string? html, string url, int statusCode = 0)
    {
        var domain = ExtractDomain(url);
        var bodyLower = html ?? string.Empty;

        // 0. RedirectLoop: a 3xx as the FINAL response is unambiguous — the HTTP
        //    client follows redirects automatically (AllowAutoRedirect, capped at
        //    MaxAutomaticRedirections), so the only way a 3xx survives to the caller
        //    is the redirect budget being exhausted by a loop / over-long chain
        //    (workspace-odn5: Cloudflare consent/bot bounce on macleans.ca). Callers
        //    that don't have a status code pass 0, so HTML-only detection skips this.
        if (statusCode >= 300 && statusCode < 400)
        {
            return new HumanActionRequired(HumanActionVariant.RedirectLoop, domain, $"HTTP {statusCode}");
        }

        // 1. RegionBlock: HTTP 451 is unambiguous; geo-restriction text is high-confidence.
        if (statusCode == 451)
        {
            return new HumanActionRequired(HumanActionVariant.RegionBlock, domain, "HTTP 451");
        }

        // workspace-lmwm: prose-prone keyword lists only count on pages small
        // enough to be an interstitial — a large article body that merely
        // MENTIONS "two-factor" or "not available in your region" is prose,
        // not a gate (same page-size gating kq4b added for captcha scoring).
        var isLargeBody = bodyLower.Length > RealPageThreshold;

        if (!isLargeBody && ContainsAny(bodyLower, RegionBlockTextIndicators))
        {
            return new HumanActionRequired(HumanActionVariant.RegionBlock, domain);
        }

        // A login form takes priority over the weaker BotBlock heuristics below: a real auth wall that
        // returns 403 with access-denied phrasing ("Access denied. Log in below:") is a Login, not a
        // hard bot block. Computed here (used again by the Login step) so the BotBlock weak-403 path can
        // exclude it.
        var hasLoginForm = ContainsAny(bodyLower, LoginFormIndicators);

        // 1c. BotBlock (workspace-3rtr): a Cloudflare/WAF HARD block — access denied outright, NOT a
        //     solvable challenge. The old code mislabeled these as "captcha" (a tiny "Sorry, you have
        //     been blocked" page scored High via the weak-indicator path) and then spun the
        //     human-action watcher waiting for the user to clear a challenge that doesn't exist. Fire
        //     ONLY when no solvable-challenge widget is present (else it's a real challenge → captcha
        //     path), on: an unambiguous access-denied marker at any size, a Cloudflare numeric error
        //     code carried as the status, or a 403 + a weaker access-denied phrase on a non-large page
        //     WITHOUT a login form (a login wall wins — see hasLoginForm above).
        if (!ContainsAny(bodyLower, SolvableChallengeIndicators))
        {
            var isCloudflareErrorStatus = statusCode is 1020 or 1015 or 1010 or 1009;
            if (ContainsAny(bodyLower, StrongBotBlockIndicators)
                || isCloudflareErrorStatus
                || (statusCode == 403 && !isLargeBody && !hasLoginForm && ContainsAny(bodyLower, WeakBotBlockIndicators)))
            {
                string detail;
                if (isCloudflareErrorStatus)
                {
                    detail = $"Cloudflare error {statusCode}";
                }
                else
                {
                    detail = statusCode == 403 ? "HTTP 403 (WAF)" : "access denied";
                }

                return new HumanActionRequired(HumanActionVariant.BotBlock, domain, detail);
            }
        }

        // 2. Captcha: small page + vendor-specific markers, OR vendor markers anywhere.
        var captchaConfidence = ScoreCaptcha(bodyLower);
        if (captchaConfidence == CaptchaConfidence.High)
        {
            return new HumanActionRequired(HumanActionVariant.Captcha, domain, "vendor markers detected");
        }

        // 3. TwoFactor: explicit OTP form markup anywhere, or verification-code
        //    prose on an interstitial-sized page (workspace-lmwm).
        if (ContainsAny(bodyLower, TwoFactorMarkupIndicators)
            || (!isLargeBody && ContainsAny(bodyLower, TwoFactorProseIndicators)))
        {
            return new HumanActionRequired(HumanActionVariant.TwoFactor, domain);
        }

        // 4. CookieConsent: explicit banner selectors.
        if (ContainsAny(bodyLower, CookieConsentIndicators))
        {
            return new HumanActionRequired(HumanActionVariant.CookieConsent, domain);
        }

        // 5. Login: HTTP 401 or 403 + login form markers (hasLoginForm computed above).
        if ((statusCode == 401 || statusCode == 403) && hasLoginForm)
        {
            return new HumanActionRequired(HumanActionVariant.Login, domain, statusCode == 401 ? "HTTP 401" : "HTTP 403");
        }

        // 6. Paywall: subscription text + at least one paywall element selector.
        if (ContainsAny(bodyLower, PaywallTextIndicators))
        {
            return new HumanActionRequired(HumanActionVariant.Paywall, domain);
        }

        // 7. Captcha (low confidence) — workspace-kq4b: this used to fall through to
        //    Generic on ANY page mentioning a noisy keyword (Cloudflare's
        //    "challenge-platform" script lands on every healthy page of a CF-fronted
        //    site), which produced a vague and demonstrably wrong "action needed"
        //    badge on thestar.com homepages and similar. The narrower path through
        //    ScoreCaptcha now only returns Low for strong indicators on large pages
        //    (e.g. a real CAPTCHA that loaded a heavyweight challenge UI). Weak
        //    indicators on large pages no longer reach this branch at all.
        if (captchaConfidence == CaptchaConfidence.Low)
        {
            return new HumanActionRequired(
                HumanActionVariant.Generic,
                domain,
                "ambiguous challenge markers on a large page");
        }

        // 8. Generic last-resort: tiny page with a single form + button (interstitial pattern).
        if (LooksLikeInterstitial(bodyLower))
        {
            return new HumanActionRequired(HumanActionVariant.Generic, domain, "interstitial-shaped HTML");
        }

        // 9. HTTP 401/403 without recognizable form — surface as Login (high cost of missing
        //    a real auth wall outweighs the false-positive cost on this hot path).
        if (statusCode == 401 || statusCode == 403)
        {
            return new HumanActionRequired(HumanActionVariant.Login, domain, statusCode == 401 ? "HTTP 401" : "HTTP 403");
        }

        return null;
    }

    /// <summary>
    /// Backwards-compatible thin wrapper preserving the prior boolean contract of
    /// <c>PageLoader.IsBotChallengePage</c>. Prefer <see cref="Detect"/> for new code.
    /// </summary>
    public static bool IsBotChallenge(string? html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return false;
        }

        return ScoreCaptcha(html) == CaptchaConfidence.High;
    }

    /// <summary>
    /// Backwards-compatible thin wrapper preserving the prior boolean contract of
    /// <c>BackgroundPreloadService.IsBotDetectionResponse</c>. Includes some of the
    /// looser-keyword variants treated as "should not cache" but not necessarily a
    /// challenge interstitial.
    /// </summary>
    public static bool IsBotDetectionResponse(string? html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return false;
        }

        return ContainsAny(html, CaptchaIndicators) || html.Contains("please enable javascript", StringComparison.OrdinalIgnoreCase);
    }

    private static CaptchaConfidence ScoreCaptcha(string html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return CaptchaConfidence.None;
        }

        var isSmall = html.Length <= RealPageThreshold;
        var isTiny = html.Length < SmallPageThreshold;
        var hasStrong = ContainsAny(html, StrongCaptchaIndicators);
        var hasWeak = ContainsAny(html, WeakCaptchaIndicators);

        // Vendor-confirmed markers on a small page → high confidence: a real
        // CAPTCHA UI loaded a heavyweight script and the page body is otherwise
        // small enough that this isn't an article that mentions the vendor name.
        if (isSmall && hasStrong)
        {
            return CaptchaConfidence.High;
        }

        // TINY pages (<5KB) mentioning "captcha" or "challenge" alone are
        // almost certainly real challenge interstitials — too small to be an
        // article that incidentally uses the word. This preserves the
        // pre-kq4b detection for plain-text challenge pages without vendor
        // scripts. Also covers weak indicators (challenge-platform, etc.) on
        // tiny pages.
        if (isTiny &&
            (html.Contains("captcha", StringComparison.OrdinalIgnoreCase)
                || html.Contains("challenge", StringComparison.OrdinalIgnoreCase)
                || hasWeak))
        {
            return CaptchaConfidence.High;
        }

        // Larger page with a STRONG vendor marker is LOW confidence: rare but
        // possible (article quoting the vendor name). Surface as Generic so
        // the user gets *some* warning, but not a confident captcha verdict.
        if (hasStrong)
        {
            return CaptchaConfidence.Low;
        }

        // Larger page with only weak markers (e.g. CF "challenge-platform" on a
        // healthy thestar.com homepage) is NOT a signal. Returning None here is
        // the fix for the workspace-kq4b false-positive badge.
        return CaptchaConfidence.None;
    }

    private static bool LooksLikeInterstitial(string html)
    {
        if (string.IsNullOrEmpty(html) || html.Length > SmallPageThreshold * 4)
        {
            return false;
        }

        var formCount = CountOccurrences(html, "<form");
        var buttonCount = CountOccurrences(html, "<button");
        var inputCount = CountOccurrences(html, "<input");

        // Single form + at most one button + few inputs is the classic interstitial shape.
        return formCount == 1 && buttonCount <= 2 && inputCount <= 4;
    }

    private static bool ContainsAny(string html, string[] needles)
        => Array.Exists(needles, needle => html.Contains(needle, StringComparison.OrdinalIgnoreCase));

    private static int CountOccurrences(string haystack, string needle)
    {
        if (string.IsNullOrEmpty(haystack) || string.IsNullOrEmpty(needle))
        {
            return 0;
        }

        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private static string ExtractDomain(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return string.Empty;
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return uri.Host;
        }

        return url.Length > 60 ? url[..60] : url;
    }

#pragma warning disable SA1201 // Enums should follow standard ordering
    private enum CaptchaConfidence
    {
        None,
        Low,
        High,
    }
#pragma warning restore SA1201
}
