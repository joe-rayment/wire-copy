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

    private static readonly string[] CaptchaIndicators =
    {
        // DataDome
        "captcha-delivery.com",
        "geo.captcha-delivery.com",
        "datadome",

        // Cloudflare
        "cf-challenge",
        "cf-browser-verification",
        "challenge-platform",
        "attention required! | cloudflare",
        "checking your browser",
        "just a moment...",

        // hCaptcha / reCAPTCHA explicit markers
        "g-recaptcha",
        "h-captcha",
        "hcaptcha.com",

        // Generic
        "you have been blocked",
        "access denied",
        "enable cookies",
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

    private static readonly string[] TwoFactorIndicators =
    {
        "autocomplete=\"one-time-code\"",
        "autocomplete='one-time-code'",
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

        // 1. RegionBlock: HTTP 451 is unambiguous; geo-restriction text is high-confidence.
        if (statusCode == 451)
        {
            return new HumanActionRequired(HumanActionVariant.RegionBlock, domain, "HTTP 451");
        }

        if (ContainsAny(bodyLower, RegionBlockTextIndicators))
        {
            return new HumanActionRequired(HumanActionVariant.RegionBlock, domain);
        }

        // 2. Captcha: small page + vendor-specific markers, OR vendor markers anywhere.
        var captchaConfidence = ScoreCaptcha(bodyLower);
        if (captchaConfidence == CaptchaConfidence.High)
        {
            return new HumanActionRequired(HumanActionVariant.Captcha, domain);
        }

        // 3. TwoFactor: explicit OTP autocomplete or verification-code copy.
        if (ContainsAny(bodyLower, TwoFactorIndicators))
        {
            return new HumanActionRequired(HumanActionVariant.TwoFactor, domain);
        }

        // 4. CookieConsent: explicit banner selectors.
        if (ContainsAny(bodyLower, CookieConsentIndicators))
        {
            return new HumanActionRequired(HumanActionVariant.CookieConsent, domain);
        }

        // 5. Login: HTTP 401 or 403 + login form markers.
        var hasLoginForm = ContainsAny(bodyLower, LoginFormIndicators);
        if ((statusCode == 401 || statusCode == 403) && hasLoginForm)
        {
            return new HumanActionRequired(HumanActionVariant.Login, domain, statusCode == 401 ? "HTTP 401" : "HTTP 403");
        }

        // 6. Paywall: subscription text + at least one paywall element selector.
        if (ContainsAny(bodyLower, PaywallTextIndicators))
        {
            return new HumanActionRequired(HumanActionVariant.Paywall, domain);
        }

        // 7. Captcha (low confidence) — falls through to Generic, since misidentifying a
        //    cookie banner as "solve the captcha" is worse than vague-but-actionable copy.
        if (captchaConfidence == CaptchaConfidence.Low)
        {
            return new HumanActionRequired(HumanActionVariant.Generic, domain);
        }

        // 8. Generic last-resort: tiny page with a single form + button (interstitial pattern).
        if (LooksLikeInterstitial(bodyLower))
        {
            return new HumanActionRequired(HumanActionVariant.Generic, domain);
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

        // Real article pages can contain "challenge-platform" injected by Cloudflare's
        // ongoing bot-monitoring scripts even on logged-in normal pages — so we require
        // small page size before declaring HIGH confidence on those keyword hits.
        var isSmall = html.Length <= RealPageThreshold;

        // Vendor-specific markers: high confidence when on a small page.
        if (isSmall)
        {
            if (html.Contains("captcha-delivery.com", StringComparison.OrdinalIgnoreCase) ||
                html.Contains("datadome", StringComparison.OrdinalIgnoreCase) ||
                html.Contains("cf-challenge", StringComparison.OrdinalIgnoreCase) ||
                html.Contains("challenge-platform", StringComparison.OrdinalIgnoreCase) ||
                html.Contains("g-recaptcha", StringComparison.OrdinalIgnoreCase) ||
                html.Contains("h-captcha", StringComparison.OrdinalIgnoreCase) ||
                html.Contains("hcaptcha.com", StringComparison.OrdinalIgnoreCase))
            {
                return CaptchaConfidence.High;
            }

            // Generic captcha keywords on very small pages.
            if (html.Length < SmallPageThreshold &&
                (html.Contains("captcha", StringComparison.OrdinalIgnoreCase) ||
                 html.Contains("challenge", StringComparison.OrdinalIgnoreCase)))
            {
                return CaptchaConfidence.High;
            }

            // Cloudflare/blocked text hints — high confidence on small pages.
            if (html.Contains("attention required! | cloudflare", StringComparison.OrdinalIgnoreCase) ||
                html.Contains("checking your browser", StringComparison.OrdinalIgnoreCase) ||
                html.Contains("just a moment...", StringComparison.OrdinalIgnoreCase) ||
                html.Contains("you have been blocked", StringComparison.OrdinalIgnoreCase) ||
                html.Contains("cf-browser-verification", StringComparison.OrdinalIgnoreCase))
            {
                return CaptchaConfidence.High;
            }
        }

        // Larger page mentioning bot-style keywords is LOW confidence — could be an
        // article quoting the words. Surface as Generic, never as a confident captcha.
        if (ContainsAny(html, CaptchaIndicators))
        {
            return CaptchaConfidence.Low;
        }

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
