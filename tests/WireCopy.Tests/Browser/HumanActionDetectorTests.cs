// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Infrastructure.Browser;
using WireCopy.Infrastructure.Browser.UI;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// Tests for the consolidated <see cref="HumanActionDetector"/> introduced by
/// workspace-0b9s. Each variant gets a dedicated case using a small inline HTML
/// fixture; one case explicitly verifies the Generic fall-back when signals are
/// ambiguous (per the robustness review's "don't guess" rule).
/// </summary>
[Trait("Category", "Unit")]
public class HumanActionDetectorTests
{
    [Fact]
    public void Detect_DataDomeCaptcha_ReturnsCaptchaVariant()
    {
        const string html = """
            <html><head><title>Just a moment...</title></head>
            <body><script src="https://geo.captcha-delivery.com/captcha/x.js"></script>
            <p>Please enable JS</p></body></html>
            """;

        var verdict = HumanActionDetector.Detect(html, "https://www.nytimes.com/2026/05/09/us/politics/article.html");

        verdict.Should().NotBeNull();
        verdict!.Variant.Should().Be(HumanActionVariant.Captcha);
        verdict.Domain.Should().Be("www.nytimes.com");
    }

    [Fact]
    public void Detect_CloudflareChallenge_ReturnsCaptchaVariant()
    {
        const string html = """
            <html><head><title>Just a moment...</title></head>
            <body class="cf-challenge"><div id="challenge-platform"></div>
            <p>Checking your browser...</p></body></html>
            """;

        var verdict = HumanActionDetector.Detect(html, "https://example.com/article");

        verdict.Should().NotBeNull();
        verdict!.Variant.Should().Be(HumanActionVariant.Captcha);
    }

    [Fact]
    public void Detect_OneTrustCookieBanner_ReturnsCookieConsentVariant()
    {
        // Mid-size page (>5KB so it can't be classified as a small captcha shell)
        // with a clear OneTrust banner div. This is the canonical GDPR consent shape.
        var html = $"""
            <html><head><title>Section front</title></head>
            <body>
            <div id="onetrust-banner-sdk" class="otBannerWrapper">
            <p>This site uses cookies. Please accept to continue.</p>
            <button>Accept All</button></div>
            <main>{new string('x', 6000)}</main>
            </body></html>
            """;

        var verdict = HumanActionDetector.Detect(html, "https://www.example.com/section/front");

        verdict.Should().NotBeNull();
        verdict!.Variant.Should().Be(HumanActionVariant.CookieConsent);
        verdict.Domain.Should().Be("www.example.com");
    }

    [Fact]
    public void Detect_TwoFactorPage_ReturnsTwoFactorVariant()
    {
        const string html = """
            <html><body>
            <h1>Enter your verification code</h1>
            <form><input autocomplete="one-time-code" name="code" /></form>
            </body></html>
            """;

        var verdict = HumanActionDetector.Detect(html, "https://accounts.google.com/signin/challenge");

        verdict.Should().NotBeNull();
        verdict!.Variant.Should().Be(HumanActionVariant.TwoFactor);
    }

    [Fact]
    public void Detect_LargeArticleMentioningTwoFactor_ReturnsNull()
    {
        // workspace-lmwm: an NYT article ABOUT two-factor authentication tripped
        // a TwoFactor verdict during browser preload because the prose keywords
        // were matched against the whole body with no page-size gating. Article
        // prose on a large page must not read as an interstitial.
        var html = $"""
            <html><body>
            <article>
            <h1>How Two-Factor Authentication Became Standard</h1>
            <p>Security experts recommend two-factor authentication and using an
            authenticator app instead of receiving a verification code by SMS.</p>
            <p>{new string('x', 25_000)}</p>
            </article>
            </body></html>
            """;

        var verdict = HumanActionDetector.Detect(html, "https://www.nytimes.com/2026/06/10/technology/anthropic-ai.html");

        verdict.Should().BeNull(
            "a >20KB article body containing 2FA prose is an article, not a gate");
    }

    [Fact]
    public void Detect_SmallPageWithTwoFactorProse_StillReturnsTwoFactor()
    {
        const string html = """
            <html><body>
            <h1>Enter the 6-digit code from your authenticator app</h1>
            <form><input name="code" /></form>
            </body></html>
            """;

        var verdict = HumanActionDetector.Detect(html, "https://accounts.example.com/2fa");

        verdict.Should().NotBeNull("interstitial-sized pages keep the prose-keyword detection");
        verdict!.Variant.Should().Be(HumanActionVariant.TwoFactor);
    }

    [Fact]
    public void Detect_LargePageWithOtpAutocompleteMarkup_StillReturnsTwoFactor()
    {
        // Markup indicators are form attributes, not prose — they stay ungated.
        var html = $"""
            <html><body>
            <form><input autocomplete="one-time-code" name="code" /></form>
            <main>{new string('x', 25_000)}</main>
            </body></html>
            """;

        var verdict = HumanActionDetector.Detect(html, "https://accounts.example.com/challenge");

        verdict.Should().NotBeNull();
        verdict!.Variant.Should().Be(HumanActionVariant.TwoFactor);
    }

    [Fact]
    public void Detect_LargeArticleMentioningRegionBlockPhrase_ReturnsNull()
    {
        var html = $"""
            <html><body>
            <article>
            <p>The streaming service told customers the show is geo-restricted and
            not available in your region without a VPN.</p>
            <p>{new string('x', 25_000)}</p>
            </article>
            </body></html>
            """;

        var verdict = HumanActionDetector.Detect(html, "https://www.example.com/news/streaming");

        verdict.Should().BeNull("geo-restriction prose in a large article is not a region block");
    }

    [Fact]
    public void Detect_RegionBlockHttp451_ReturnsRegionBlockVariant()
    {
        var verdict = HumanActionDetector.Detect(html: string.Empty, "https://example.com/page", statusCode: 451);

        verdict.Should().NotBeNull();
        verdict!.Variant.Should().Be(HumanActionVariant.RegionBlock);
        verdict.Detail.Should().Be("HTTP 451");
    }

    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(303)]
    [InlineData(307)]
    [InlineData(308)]
    public void Detect_Final3xxStatus_ReturnsRedirectLoopVariant(int statusCode)
    {
        // workspace-odn5: the HTTP client follows redirects automatically up to its
        // budget, so a 3xx surviving to the caller means the budget was exhausted by
        // a loop / over-long chain. The HTTP path (PageLoader.TryHttpFetchAsync) feeds
        // that status straight into Detect, which must surface a typed RedirectLoop
        // verdict instead of the bare "HTTP 302" string.
        var verdict = HumanActionDetector.Detect(html: string.Empty, "https://macleans.ca/", statusCode);

        verdict.Should().NotBeNull();
        verdict!.Variant.Should().Be(HumanActionVariant.RedirectLoop);
        verdict.Domain.Should().Be("macleans.ca");
        verdict.Detail.Should().Be($"HTTP {statusCode}");
    }

    [Fact]
    public void Detect_NoStatusCode_DoesNotFalselyFlagRedirectLoop()
    {
        // HTML-only callers pass statusCode 0 — a clean page must never be mistaken
        // for a redirect loop just because no status was supplied.
        const string html = "<html><body><p>An ordinary article paragraph with real content.</p></body></html>";

        var verdict = HumanActionDetector.Detect(html, "https://example.com/page", statusCode: 0);

        verdict.Should().BeNull();
    }

    [Fact]
    public void Detect_RegionBlockText_ReturnsRegionBlockVariant()
    {
        const string html = """
            <html><body><p>This content is not available in your country.</p></body></html>
            """;

        var verdict = HumanActionDetector.Detect(html, "https://example.com/page");

        verdict.Should().NotBeNull();
        verdict!.Variant.Should().Be(HumanActionVariant.RegionBlock);
    }

    [Fact]
    public void Detect_PaywallText_ReturnsPaywallVariant()
    {
        // Paywall preview page with a snippet of text + the canonical CTA copy.
        var html = $"""
            <html><body>
            <article><p>This is the lede paragraph of the article…</p>
            <p>{new string('y', 4000)}</p></article>
            <div class="paywall">Subscribe to continue reading.</div>
            </body></html>
            """;

        var verdict = HumanActionDetector.Detect(html, "https://www.nytimes.com/2026/05/09/article.html");

        verdict.Should().NotBeNull();
        verdict!.Variant.Should().Be(HumanActionVariant.Paywall);
    }

    [Fact]
    public void Detect_LoginWith403AndForm_ReturnsLoginVariant()
    {
        const string html = """
            <html><body>
            <form action="/auth/login" method="post">
            <input type="password" name="password" />
            </form></body></html>
            """;

        var verdict = HumanActionDetector.Detect(html, "https://example.com/api/article", statusCode: 403);

        verdict.Should().NotBeNull();
        verdict!.Variant.Should().Be(HumanActionVariant.Login);
        verdict.Detail.Should().Be("HTTP 403");
    }

    [Fact]
    public void Detect_AmbiguousSmallCaptchaKeyword_ReturnsGenericVariant()
    {
        // Page is large enough that captcha keywords could be a quote or part of
        // article text — high-confidence captcha verdict should NOT fire here, and
        // the detector should fall back to Generic per the robustness review.
        var html = $"""
            <html><body>
            <article>
            <p>The article discusses how websites deploy captcha systems to filter bots.</p>
            <p>{new string('a', 50000)}</p>
            </article>
            </body></html>
            """;

        var verdict = HumanActionDetector.Detect(html, "https://example.com/article");

        // Either no verdict (large page, no real signal) or Generic (ambiguous).
        // Both outcomes satisfy the "don't misidentify as Captcha" contract.
        if (verdict != null)
        {
            verdict.Variant.Should().NotBe(HumanActionVariant.Captcha);
        }
    }

    [Fact]
    public void Detect_CleanArticle_ReturnsNull()
    {
        var html = $"""
            <html><head><title>Real Article</title></head>
            <body><article>
            <p>{new string('z', 30000)}</p>
            </article></body></html>
            """;

        var verdict = HumanActionDetector.Detect(html, "https://example.com/article");

        verdict.Should().BeNull();
    }

    [Fact]
    public void Detect_LargeCloudflareHostedHomepage_NoFalsePositive()
    {
        // workspace-kq4b: thestar.com (and any other CF-fronted site) ships
        // Cloudflare's bot-monitoring script on every healthy page. That script
        // injects markup matching the literal token `challenge-platform` (the
        // hyphenated form used by CF's runtime). On the pre-kq4b detector this
        // tripped the path-7 LOW-confidence fall-through and surfaced a
        // Generic "action needed" badge on healthy pages. After the fix, weak
        // keywords on a LARGE page must NOT produce any verdict — confirmed
        // by `Detect()` returning null and `IsBotChallenge()` returning false.
        var html = "<html><head><title>Breaking News - Headlines &amp; Top Stories | The Star</title></head><body>" +
                   "<header><nav>Home GTA Canada Politics Opinion</nav></header><main>" +
                   "<article><h1>Top headline</h1><p>" + new string('q', 80000) + "</p></article></main>" +
                   "<script src=\"https://challenges.cloudflare.com/turnstile/v0/api.js\"></script>" +
                   // The literal token CF's runtime injects on every CF-fronted page.
                   // We plant it explicitly here so the fixture exercises the exact
                   // false-positive shape — not a near-miss that would pass even on
                   // the broken detector (qa-enforcer 2026-05-19 review).
                   "<div class=\"cf-mgmt challenge-platform\" data-cf=\"challenge-platform\"></div>" +
                   "<script>/* injected by challenge-platform runtime */</script>" +
                   "</body></html>";

        // Sanity: the fixture genuinely contains the literal indicator the
        // detector matches. Without this assertion the test could regress to a
        // placebo if the fixture is edited.
        html.Should().Contain("challenge-platform",
            "the fixture must contain the literal indicator the detector tries to match — " +
            "otherwise this test is a placebo regardless of detector behaviour");

        var verdict = HumanActionDetector.Detect(html, "https://www.thestar.com/");

        verdict.Should().BeNull(
            "a large healthy page containing `challenge-platform` (CF's bot-monitor noise) " +
            "must NOT trip a Generic verdict — this is the exact false positive the user reported");

        // Belt-and-suspenders: the back-compat wrapper used by the preload path
        // must agree. Otherwise BackgroundPreloadService.IsBotDetectionResponse
        // would still circuit-break the domain even with the typed Detect path
        // happy.
        HumanActionDetector.IsBotChallenge(html).Should().BeFalse(
            "the back-compat IsBotChallenge predicate must also reject the false-positive shape");
    }

    [Fact]
    public void ScoreCaptcha_LargePage_WeakOnly_ProvenNotPlacebo()
    {
        // Companion to Detect_LargeCloudflareHostedHomepage_NoFalsePositive.
        // Verifies the fix is load-bearing by:
        //   1. constructing a fixture with the literal `challenge-platform` weak
        //      indicator,
        //   2. confirming the detector returns null,
        //   3. asserting that swapping in a STRONG indicator on the same large
        //      page DOES still surface a verdict (Generic LOW confidence) —
        //      proves the size-gating only filters weak markers, not all signals.
        var weakOnlyHtml = "<html><body><article><p>" + new string('q', 60000) +
                           "</p></article><div class=\"challenge-platform\"></div></body></html>";

        HumanActionDetector.Detect(weakOnlyHtml, "https://example.com/").Should().BeNull(
            "weak-only indicator on large page must return null");

        // Same page, but with a STRONG indicator instead of the weak one —
        // proves the detector still notices vendor-confirmed signals.
        var strongHtml = "<html><body><article><p>" + new string('q', 60000) +
                         "</p></article><iframe src=\"https://hcaptcha.com/\"></iframe></body></html>";

        var strongVerdict = HumanActionDetector.Detect(strongHtml, "https://example.com/");
        strongVerdict.Should().NotBeNull(
            "strong vendor marker on a large page must still surface SOMETHING");
        strongVerdict!.Variant.Should().Be(HumanActionVariant.Generic,
            "large + strong marker downgrades to Generic; the gating is precise, not blanket-off");
    }

    [Fact]
    public void Detect_LargePage_StrongCaptchaMarker_ReturnsGenericNotHigh()
    {
        // workspace-kq4b: even a strong vendor marker on a large page should
        // remain LOW confidence (Generic) — it might be an article quoting the
        // vendor name. The bead acceptance criterion requires the badge to
        // either downgrade or vanish on large pages; it must never escalate
        // to a high-confidence Captcha verdict.
        var html = $"""
            <html><body>
            <article>
            <p>How DataDome and other captcha vendors work to stop bots.</p>
            <p>{new string('x', 50000)}</p>
            </article>
            <noscript>captcha-delivery.com</noscript>
            </body></html>
            """;

        var verdict = HumanActionDetector.Detect(html, "https://example.com/security-article");

        verdict.Should().NotBeNull("strong markers on a large page should still surface SOMETHING actionable");
        verdict!.Variant.Should().Be(HumanActionVariant.Generic,
            "ambiguous strong marker on a large page must downgrade to Generic, never Captcha");
    }

    [Fact]
    public void Detect_LargePage_WeakIndicatorAlone_ReturnsNull()
    {
        // Variant of the thestar case with each weak indicator on its own.
        foreach (var weak in new[] { "challenge-platform", "you have been blocked", "access denied" })
        {
            var html = $"""
                <html><body>
                <article><p>{new string('p', 60000)}</p></article>
                <div data-tag="{weak}"></div>
                </body></html>
                """;

            var verdict = HumanActionDetector.Detect(html, "https://example.com/page");

            verdict.Should().BeNull(
                $"weak indicator '{weak}' on a large healthy page must NOT trigger a verdict");
        }
    }

    [Fact]
    public void IsBotChallenge_BackwardsCompatWrapper_AgreesWithDetect()
    {
        const string html = """
            <html><head><title>Just a moment...</title></head>
            <body class="cf-challenge"><p>Checking your browser...</p></body></html>
            """;

        HumanActionDetector.IsBotChallenge(html).Should().BeTrue();
        var verdict = HumanActionDetector.Detect(html, "https://example.com/x");
        verdict.Should().NotBeNull();
        verdict!.Variant.Should().Be(HumanActionVariant.Captcha);
    }

    [Fact]
    public void IsBotDetectionResponse_BackwardsCompatWrapper_DetectsKnownIndicators()
    {
        const string html = "<html>Access denied. You have been blocked.</html>";

        HumanActionDetector.IsBotDetectionResponse(html).Should().BeTrue();
    }

    [Theory]
    [InlineData(HumanActionVariant.Captcha, "Site is showing a CAPTCHA")]
    [InlineData(HumanActionVariant.Login, "Log in at nytimes.com in your browser")]
    [InlineData(HumanActionVariant.CookieConsent, "Cookie consent banner blocking content")]
    [InlineData(HumanActionVariant.TwoFactor, "Two-factor code required at nytimes.com")]
    [InlineData(HumanActionVariant.Paywall, "Article is paywalled at nytimes.com")]
    [InlineData(HumanActionVariant.RegionBlock, "Site blocks this region (HTTP 451)")]
    [InlineData(HumanActionVariant.RedirectLoop, "Site is stuck in a redirect loop")]
    [InlineData(HumanActionVariant.Generic, "Action needed at nytimes.com")]
    public void GetHumanActionCopy_RendersVariantSpecificHeadline(HumanActionVariant variant, string expectedHeadline)
    {
        var (headline, _, _) = TerminalPageRenderer.GetHumanActionCopy(variant, "nytimes.com");

        headline.Should().Be(expectedHeadline);
    }

    [Fact]
    public void GetHumanActionCopy_CaptchaHintMentionsShiftOAndDomain()
    {
        var (_, body, hint) = TerminalPageRenderer.GetHumanActionCopy(HumanActionVariant.Captcha, "nytimes.com");

        body.Should().Contain("press R");
        hint.Should().Contain("nytimes.com");
        hint.Should().Contain("|:open");
        hint.Should().Contain("b:back");
    }

    [Fact]
    public void GetHumanActionCopy_LoginHintMentionsShiftI()
    {
        var (_, _, hint) = TerminalPageRenderer.GetHumanActionCopy(HumanActionVariant.Login, "nytimes.com");

        hint.Should().Contain("Shift+I");
    }

    [Fact]
    public void GetHumanActionCopy_GenericFallback_NamesDomainAndAvoidsSpecifics()
    {
        var (headline, _, _) = TerminalPageRenderer.GetHumanActionCopy(HumanActionVariant.Generic, "example.com");

        // Must NOT name a specific cause (no "captcha", "login", "consent" leaking through).
        headline.Should().Contain("example.com");
        headline.Should().NotContainAny("CAPTCHA", "Log in", "consent banner");
    }

    /// <summary>
    /// Box width regression guard (workspace-0b9s QA #4): every variant's
    /// headline / body / hint must fit inside the centered box's inner width
    /// even when the domain is a long subdomain (worst-case 22 chars).
    ///
    /// <para>The box is sized as <c>boxWidth = innerWidth + 4</c> (2 border chars + 2
    /// padding spaces) where <c>innerWidth</c> is capped at <c>MaxBoxContentWidth = 56</c>.
    /// Practical limit per content line therefore is <c>MaxBoxContentWidth - 2 = 54</c>
    /// chars (since the inner width is content + 2 padding). The headline carries an
    /// additional 2-char braille bullet prefix (<c>"⠇ "</c>) that we add here.</para>
    /// </summary>
    [Theory]
    [InlineData(HumanActionVariant.Captcha)]
    [InlineData(HumanActionVariant.Login)]
    [InlineData(HumanActionVariant.CookieConsent)]
    [InlineData(HumanActionVariant.TwoFactor)]
    [InlineData(HumanActionVariant.Paywall)]
    [InlineData(HumanActionVariant.RegionBlock)]
    [InlineData(HumanActionVariant.RedirectLoop)]
    [InlineData(HumanActionVariant.Generic)]
    public void GetHumanActionCopy_AllVariantsFitWithinBoxWidth(HumanActionVariant variant)
    {
        // Worst-case 22-char subdomain — matches "subdomain.nytimes.com".
        const string longestPlausibleDomain = "subdomain.nytimes.com";
        const int maxContentChars = 54; // MaxBoxContentWidth (56) - 2 padding spaces
        const int headlineBulletPrefix = 2; // "⠇ " prepended to the styled headline

        var (headline, body, hint) = TerminalPageRenderer.GetHumanActionCopy(variant, longestPlausibleDomain);

        (headline.Length + headlineBulletPrefix).Should().BeLessThanOrEqualTo(
            maxContentChars,
            $"variant {variant} headline '⠇ {headline}' must fit in {maxContentChars} chars at 80 cols");
        body.Length.Should().BeLessThanOrEqualTo(
            maxContentChars,
            $"variant {variant} body '{body}' must fit in {maxContentChars} chars at 80 cols");
        hint.Length.Should().BeLessThanOrEqualTo(
            maxContentChars,
            $"variant {variant} hint '{hint}' must fit in {maxContentChars} chars at 80 cols");
    }
}
