// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using WireCopy.Application.Interfaces;
using WireCopy.Infrastructure.Browser;
using WireCopy.Infrastructure.Configuration;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// Tests for <see cref="AutoCookieRefresher"/> — the service that auto-imports
/// cookies when the foreground browser lands on a paywalled domain in a
/// logged-in state.
/// </summary>
[Trait("Category", "Unit")]
public class AutoCookieRefresherTests
{
    private readonly IBrowserSession _browserSession;
    private readonly ICookieManager _cookieManager;
    private readonly IHttpCookieRefresher _httpRefresher;
    private readonly BrowserConfiguration _browserConfig;
    private readonly NavigationService _navigationService;
    private readonly FakeTimeProvider _time;

    public AutoCookieRefresherTests()
    {
        _browserSession = Substitute.For<IBrowserSession>();
        _cookieManager = Substitute.For<ICookieManager>();
        _httpRefresher = Substitute.For<IHttpCookieRefresher>();
        _browserConfig = new BrowserConfiguration
        {
            PaywalledDomains = new[] { "nytimes.com", "wsj.com" },
        };
        _navigationService = new NavigationService(Substitute.For<ILogger<NavigationService>>());
        _time = new FakeTimeProvider(new DateTime(2026, 4, 28, 12, 0, 0, DateTimeKind.Utc));

        // Default: browser context active, returning a couple of cookies for NYT.
        _browserSession.HasBrowserContext.Returns(true);
        _browserSession.GetCookiesForUrlAsync("https://nytimes.com/").Returns(
            Task.FromResult<IReadOnlyList<StoredCookie>>(new[]
            {
                new StoredCookie("nyt-s", "abc", ".nytimes.com", "/", DateTime.UtcNow.AddDays(30)),
                new StoredCookie("nyt-a", "def", ".nytimes.com", "/", DateTime.UtcNow.AddDays(30)),
            }));
        _browserSession.GetCookiesForUrlAsync("https://wsj.com/").Returns(
            Task.FromResult<IReadOnlyList<StoredCookie>>(Array.Empty<StoredCookie>()));
    }

    private AutoCookieRefresher CreateSut() => new(
        _browserSession,
        _cookieManager,
        _httpRefresher,
        _browserConfig,
        _navigationService,
        Substitute.For<ILogger<AutoCookieRefresher>>(),
        _time);

    [Fact]
    public async Task LoggedInPaywalledDomain_NoRecentImport_TriggersImport()
    {
        var sut = CreateSut();

        var triggered = await sut.MaybeRefreshAsync(
            "https://www.nytimes.com/2026/04/01/world/example.html",
            HtmlFixtures.NytLoggedInArticle());

        triggered.Should().BeTrue();
        await _cookieManager.Received(1).SaveCookiesAsync(
            Arg.Is<IReadOnlyList<StoredCookie>>(c => c.Count == 2),
            Arg.Any<CancellationToken>());
        await _httpRefresher.Received(1).RefreshAsync();
        _navigationService.CurrentContext.StatusMessage.Should().Contain("Cookies refreshed");
    }

    [Fact]
    public async Task NonPaywalledDomain_DoesNotTriggerImport()
    {
        var sut = CreateSut();

        var triggered = await sut.MaybeRefreshAsync(
            "https://example.com/article",
            HtmlFixtures.NytLoggedInArticle());

        triggered.Should().BeFalse();
        await _cookieManager.DidNotReceive().SaveCookiesAsync(
            Arg.Any<IReadOnlyList<StoredCookie>>(), Arg.Any<CancellationToken>());
        await _httpRefresher.DidNotReceive().RefreshAsync();
    }

    [Fact]
    public async Task PaywallGatePresent_DoesNotTriggerImport()
    {
        var sut = CreateSut();

        var triggered = await sut.MaybeRefreshAsync(
            "https://www.nytimes.com/2026/04/01/world/gated.html",
            HtmlFixtures.NytPaywalledArticle());

        triggered.Should().BeFalse();
        await _cookieManager.DidNotReceive().SaveCookiesAsync(
            Arg.Any<IReadOnlyList<StoredCookie>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ThinContent_DoesNotTriggerImport()
    {
        var sut = CreateSut();

        // Account link present, no paywall gate, but body word count < 500.
        var html = "<html><body><a href='/account/'>Account</a><p>Short.</p></body></html>";
        var triggered = await sut.MaybeRefreshAsync(
            "https://www.nytimes.com/2026/04/01/world/short.html",
            html);

        triggered.Should().BeFalse();
        await _cookieManager.DidNotReceive().SaveCookiesAsync(
            Arg.Any<IReadOnlyList<StoredCookie>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NoAccountLink_DoesNotTriggerImport()
    {
        var sut = CreateSut();

        // Lots of words, no paywall gate, but no account link → not logged-in.
        var body = string.Join(" ", Enumerable.Repeat("word", 800));
        var html = $"<html><body><article>{body}</article></body></html>";

        var triggered = await sut.MaybeRefreshAsync(
            "https://www.nytimes.com/2026/04/01/world/anon.html",
            html);

        triggered.Should().BeFalse();
        await _cookieManager.DidNotReceive().SaveCookiesAsync(
            Arg.Any<IReadOnlyList<StoredCookie>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecentImport_SkipsDueToCooldown()
    {
        var sut = CreateSut();

        var firstTriggered = await sut.MaybeRefreshAsync(
            "https://www.nytimes.com/2026/04/01/world/a.html",
            HtmlFixtures.NytLoggedInArticle());
        firstTriggered.Should().BeTrue();

        // Advance only 1 hour — well inside the 24h cooldown.
        _time.Advance(TimeSpan.FromHours(1));

        _cookieManager.ClearReceivedCalls();
        _httpRefresher.ClearReceivedCalls();

        var secondTriggered = await sut.MaybeRefreshAsync(
            "https://www.nytimes.com/2026/04/01/world/b.html",
            HtmlFixtures.NytLoggedInArticle());

        secondTriggered.Should().BeFalse();
        await _cookieManager.DidNotReceive().SaveCookiesAsync(
            Arg.Any<IReadOnlyList<StoredCookie>>(), Arg.Any<CancellationToken>());
        await _httpRefresher.DidNotReceive().RefreshAsync();
    }

    [Fact]
    public async Task CooldownElapsed_TriggersAgain()
    {
        var sut = CreateSut();

        var first = await sut.MaybeRefreshAsync(
            "https://www.nytimes.com/2026/04/01/world/a.html",
            HtmlFixtures.NytLoggedInArticle());
        first.Should().BeTrue();

        // Advance past cooldown window.
        _time.Advance(AutoCookieRefresher.CooldownWindow + TimeSpan.FromMinutes(1));

        _cookieManager.ClearReceivedCalls();
        _httpRefresher.ClearReceivedCalls();

        var second = await sut.MaybeRefreshAsync(
            "https://www.nytimes.com/2026/04/02/world/b.html",
            HtmlFixtures.NytLoggedInArticle());

        second.Should().BeTrue();
        await _cookieManager.Received(1).SaveCookiesAsync(
            Arg.Any<IReadOnlyList<StoredCookie>>(), Arg.Any<CancellationToken>());
        await _httpRefresher.Received(1).RefreshAsync();
    }

    [Fact]
    public async Task NoBrowserContext_DoesNotTriggerImport()
    {
        _browserSession.HasBrowserContext.Returns(false);
        var sut = CreateSut();

        var triggered = await sut.MaybeRefreshAsync(
            "https://www.nytimes.com/2026/04/01/world/a.html",
            HtmlFixtures.NytLoggedInArticle());

        triggered.Should().BeFalse();
        await _cookieManager.DidNotReceive().SaveCookiesAsync(
            Arg.Any<IReadOnlyList<StoredCookie>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BrowserReturnsZeroCookies_StillCountsAsAttempt_NoSavePerformed()
    {
        _browserSession.GetCookiesForUrlAsync("https://nytimes.com/").Returns(
            Task.FromResult<IReadOnlyList<StoredCookie>>(Array.Empty<StoredCookie>()));
        var sut = CreateSut();

        var triggered = await sut.MaybeRefreshAsync(
            "https://www.nytimes.com/2026/04/01/world/a.html",
            HtmlFixtures.NytLoggedInArticle());

        triggered.Should().BeTrue();
        await _cookieManager.DidNotReceive().SaveCookiesAsync(
            Arg.Any<IReadOnlyList<StoredCookie>>(), Arg.Any<CancellationToken>());
        await _httpRefresher.DidNotReceive().RefreshAsync();
    }

    [Fact]
    public void LoggedInPaywallDetector_DetectsLoggedInArticle()
    {
        LoggedInPaywallDetector.LooksLoggedIn(HtmlFixtures.NytLoggedInArticle())
            .Should().BeTrue();
    }

    [Fact]
    public void LoggedInPaywallDetector_RejectsPaywalledArticle()
    {
        LoggedInPaywallDetector.LooksLoggedIn(HtmlFixtures.NytPaywalledArticle())
            .Should().BeFalse();
    }

    [Fact]
    public void LoggedInPaywallDetector_RejectsEmptyHtml()
    {
        LoggedInPaywallDetector.LooksLoggedIn(null).Should().BeFalse();
        LoggedInPaywallDetector.LooksLoggedIn(string.Empty).Should().BeFalse();
        LoggedInPaywallDetector.LooksLoggedIn("   ").Should().BeFalse();
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;

        public FakeTimeProvider(DateTime utcNow)
        {
            _now = new DateTimeOffset(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc));
        }

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan span) => _now = _now.Add(span);
    }
}

/// <summary>
/// Realistic-shape HTML fixtures for paywalled-domain detection tests. These
/// approximate what NYT serves to a logged-in user (account link, full body)
/// vs. an anonymous user (paywall gate, truncated preview).
/// </summary>
internal static class HtmlFixtures
{
    public static string NytLoggedInArticle()
    {
        // ~600+ words of body text plus an account link; no paywall gate element.
        var body = string.Join(' ', Enumerable.Repeat(
            "The economy continues to expand at a steady pace as analysts weigh the impact of recent policy changes.",
            12));

        return $$"""
            <!DOCTYPE html>
            <html>
            <head><title>Example NYT Article</title></head>
            <body>
              <header>
                <nav>
                  <a href="/section/world/">World</a>
                  <a href="/account/" data-testid="my-account">My Account</a>
                </nav>
              </header>
              <article data-testid="article-body">
                <h1>An Example Headline</h1>
                <p>{{body}}</p>
                <p>{{body}}</p>
                <p>{{body}}</p>
                <p>{{body}}</p>
                <p>{{body}}</p>
              </article>
            </body>
            </html>
            """;
    }

    public static string NytPaywalledArticle()
    {
        // Anonymous user view: short preview followed by a gateway element.
        // The presence of a paywall element should kill detection regardless of
        // any account link that might otherwise appear (login CTA).
        return """
            <!DOCTYPE html>
            <html>
            <head><title>Example NYT Article</title></head>
            <body>
              <header>
                <nav>
                  <a href="/section/world/">World</a>
                </nav>
              </header>
              <article>
                <h1>An Example Headline</h1>
                <p>This is a short preview paragraph before the gate appears.</p>
                <p>Subscribers can read every article in full.</p>
              </article>
              <div class="gateway" data-testid="gateway-block">
                <p>Subscribe to continue reading.</p>
                <a href="/subscription/">Subscribe</a>
              </div>
            </body>
            </html>
            """;
    }
}
