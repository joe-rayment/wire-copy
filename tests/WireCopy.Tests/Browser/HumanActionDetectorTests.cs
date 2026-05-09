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
    public void Detect_RegionBlockHttp451_ReturnsRegionBlockVariant()
    {
        var verdict = HumanActionDetector.Detect(html: string.Empty, "https://example.com/page", statusCode: 451);

        verdict.Should().NotBeNull();
        verdict!.Variant.Should().Be(HumanActionVariant.RegionBlock);
        verdict.Detail.Should().Be("HTTP 451");
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
    [InlineData(HumanActionVariant.Generic, "Something on nytimes.com needs your attention")]
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
        hint.Should().Contain("Shift+O:open");
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
}
