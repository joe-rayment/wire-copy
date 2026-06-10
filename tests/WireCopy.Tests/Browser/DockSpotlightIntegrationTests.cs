// Licensed under the MIT License. See LICENSE in the repository root.

using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using WireCopy.Application.Interfaces;
using WireCopy.Infrastructure.Browser;
using WireCopy.Infrastructure.Configuration;
using Xunit;
using Xunit.Abstractions;

namespace WireCopy.Tests.Browser;

/// <summary>
/// Drives the REAL headed browser to verify the dock spotlight's contract:
/// syncing a selection highlights the matching anchor on the live page AND
/// scrolls it into the viewport — an off-screen highlight is the feature's
/// defined failure mode, asserted here as a hard requirement. Also proves
/// follow-navigation (live page ≠ TUI page) and overlay clearing.
/// Requires an X display — run under xvfb-run; self-skips otherwise:
///   xvfb-run -a ./dotnet test --filter DockSpotlight
/// </summary>
[Trait("Category", "Integration")]
[Collection(WireCopy.Tests.HeadedBrowserSerialCollection.Name)]
public class DockSpotlightIntegrationTests
{
    private readonly ITestOutputHelper _out;

    public DockSpotlightIntegrationTests(ITestOutputHelper output) => _out = output;

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task Spotlight_HighlightsScrollsFollowsAndClears()
    {
        Skip.If(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")),
            "Headed browser requires an X display — run under xvfb-run.");

        using var server = new TinySiteServer();
        var urlA = server.UrlFor("a");
        var urlB = server.UrlFor("b");

        // Sidecar=false: these tests dock explicitly via the toggle below.
        var config = Options.Create(new BrowserConfiguration { Headless = false, Sidecar = false });
        var cookieManager = Substitute.For<ICookieManager>();
        cookieManager.LoadCookiesAsync().Returns(Array.Empty<StoredCookie>());

        using var session = new BrowserSession(config, NullLogger<BrowserSession>.Instance, cookieManager);

        Microsoft.Playwright.IPage page;
        try
        {
            page = await session.GetOrCreatePageAsync(headless: false);
        }
        catch (Exception ex)
        {
            Skip.If(true, $"Headed Chromium could not launch here: {ex.Message}");
            return;
        }

        await using var spotlight = new DockSpotlight(session, NullLogger<DockSpotlight>.Instance);

        (await session.ToggleWindowDockAsync()).Should().Be(BrowserWindowState.Docked);
        session.IsDocked.Should().BeTrue();

        // workspace-qigc: the spotlight drives the dedicated LENS tab, never the
        // fetch page — all probes target the lens.
        var lens = await session.GetLensPageAsync();
        lens.Should().NotBeNull("a docked headed session must provide a lens tab");

        // --- 1. Highlight a story far below the fold: overlay appears AND the
        //        anchor is scrolled into the viewport (off-screen = failure).
        //        The lens starts blank, so this also exercises follow-navigation. ---
        spotlight.RequestSync(new SpotlightTarget(urlA, $"{urlA}story-77", "Story 77"));
        var probe77 = await WaitForSpotlightAsync(lens!, "story-77");
        _out.WriteLine($"story-77 probe: {probe77}");
        probe77.OverlayPresent.Should().BeTrue("the spotlight overlay must exist");
        probe77.AnchorInViewport.Should().BeTrue(
            "a highlighted story that is not visible on screen is the feature's defined failure");
        probe77.OverlayWrapsAnchor.Should().BeTrue("the overlay must wrap the selected anchor");

        // --- 2. Move the selection: the overlay follows and stays visible. ---
        spotlight.RequestSync(new SpotlightTarget(urlA, $"{urlA}story-5", "Story 5"));
        var probe5 = await WaitForSpotlightAsync(lens!, "story-5");
        _out.WriteLine($"story-5 probe : {probe5}");
        probe5.AnchorInViewport.Should().BeTrue();
        probe5.OverlayWrapsAnchor.Should().BeTrue();

        // --- 3. Follow-navigation: the TUI shows page B (cache hit — the lens
        //        was never navigated), the spotlight navigates it. ---
        spotlight.RequestSync(new SpotlightTarget(urlB, $"{urlB}b-story-3", "B-Story 3"));
        var probeB = await WaitForSpotlightAsync(lens!, "b-story-3");
        _out.WriteLine($"b-story-3 probe: {probeB}, lens URL: {lens!.Url}");
        lens.Url.Should().StartWith(urlB, "the lens must follow the TUI to page B");
        probeB.AnchorInViewport.Should().BeTrue();
        probeB.OverlayWrapsAnchor.Should().BeTrue();

        // --- 4. The fetch page was never touched by any of the above. ---
        page.Url.Should().NotStartWith(urlB, "the spotlight must never navigate the fetch page");

        // --- 5. Clear removes the overlay. ---
        spotlight.RequestClear();
        await WaitUntilAsync(async () => !(await ProbeAsync(lens, "b-story-3")).OverlayPresent);
        (await ProbeAsync(lens, "b-story-3")).OverlayPresent.Should().BeFalse();
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task SpotlightFollowNav_DoesNotDisturbAConcurrentFetch()
    {
        Skip.If(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")),
            "Headed browser requires an X display — run under xvfb-run.");

        using var server = new TinySiteServer();
        var urlA = server.UrlFor("a");
        var urlB = server.UrlFor("b");

        var config = Options.Create(new BrowserConfiguration { Headless = false, Sidecar = false });
        var cookieManager = Substitute.For<ICookieManager>();
        cookieManager.LoadCookiesAsync().Returns(Array.Empty<StoredCookie>());

        using var session = new BrowserSession(config, NullLogger<BrowserSession>.Instance, cookieManager);

        Microsoft.Playwright.IPage page;
        try
        {
            page = await session.GetOrCreatePageAsync(headless: false);
        }
        catch (Exception ex)
        {
            Skip.If(true, $"Headed Chromium could not launch here: {ex.Message}");
            return;
        }

        await using var spotlight = new DockSpotlight(session, NullLogger<DockSpotlight>.Instance);
        (await session.ToggleWindowDockAsync()).Should().Be(BrowserWindowState.Docked);
        var lens = await session.GetLensPageAsync();
        lens.Should().NotBeNull();

        // workspace-u4o9 regression: kick off a fetch on the foreground page and a
        // spotlight follow-navigation at the same moment — with the lens tab the two
        // must be fully independent; before, the follow-nav interrupted the load.
        var fetch = page.GotoAsync(urlA, new Microsoft.Playwright.PageGotoOptions { Timeout = 15000 });
        spotlight.RequestSync(new SpotlightTarget(urlB, $"{urlB}b-story-3", "B-Story 3"));

        var response = await fetch;
        response!.Ok.Should().BeTrue("the fetch must complete normally despite the concurrent follow-nav");
        page.Url.Should().StartWith(urlA);

        await WaitUntilAsync(() => Task.FromResult(lens!.Url.StartsWith(urlB, StringComparison.Ordinal)));
        lens!.Url.Should().StartWith(urlB, "the lens follows independently");
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task Spotlight_ReaderViewFollowOnly_NavigatesLivePageAndClearsHighlight()
    {
        Skip.If(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")),
            "Headed browser requires an X display — run under xvfb-run.");

        using var server = new TinySiteServer();
        var urlA = server.UrlFor("a");
        var urlB = server.UrlFor("b");

        var config = Options.Create(new BrowserConfiguration { Headless = false, Sidecar = false });
        var cookieManager = Substitute.For<ICookieManager>();
        cookieManager.LoadCookiesAsync().Returns(Array.Empty<StoredCookie>());

        using var session = new BrowserSession(config, NullLogger<BrowserSession>.Instance, cookieManager);

        Microsoft.Playwright.IPage page;
        try
        {
            page = await session.GetOrCreatePageAsync(headless: false);
        }
        catch (Exception ex)
        {
            Skip.If(true, $"Headed Chromium could not launch here: {ex.Message}");
            return;
        }

        await using var spotlight = new DockSpotlight(session, NullLogger<DockSpotlight>.Instance);

        (await session.ToggleWindowDockAsync()).Should().Be(BrowserWindowState.Docked);
        var lens = await session.GetLensPageAsync();
        lens.Should().NotBeNull();

        // Light up a story on page A so there is a stale overlay for the follow to clear.
        spotlight.RequestSync(new SpotlightTarget(urlA, $"{urlA}story-5", "Story 5"));
        await WaitForSpotlightAsync(lens!, "story-5");

        // workspace-nqqs: reader view emits a follow-only target (page url, no anchor). The
        // lens must FOLLOW to page B (the article the terminal is now reading — a cache hit
        // never navigated the lens) AND drop the stale highlight, because in reader view
        // the page itself is the content so nothing should be boxed.
        spotlight.RequestSync(new SpotlightTarget(urlB, urlB, string.Empty, FollowPageOnly: true));
        await WaitUntilAsync(async () =>
            lens!.Url.StartsWith(urlB, StringComparison.Ordinal)
            && !(await ProbeAsync(lens, "b-story-3")).OverlayPresent);

        lens!.Url.Should().StartWith(urlB, "the lens must follow the reader view to the article being read");
        (await ProbeAsync(lens, "b-story-3")).OverlayPresent.Should().BeFalse(
            "follow-only reader view draws no highlight box — the page itself is the content");
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task UserDrivenLensNavigation_IsNeverYankedBack_AndOffersAdoption()
    {
        Skip.If(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")),
            "Headed browser requires an X display — run under xvfb-run.");

        using var server = new TinySiteServer();
        var urlA = server.UrlFor("a");
        var urlB = server.UrlFor("b");

        var config = Options.Create(new BrowserConfiguration { Headless = false, Sidecar = false });
        var cookieManager = Substitute.For<ICookieManager>();
        cookieManager.LoadCookiesAsync().Returns(Array.Empty<StoredCookie>());

        using var session = new BrowserSession(config, NullLogger<BrowserSession>.Instance, cookieManager);
        try
        {
            await session.GetOrCreatePageAsync(headless: false);
        }
        catch (Exception ex)
        {
            Skip.If(true, $"Headed Chromium could not launch here: {ex.Message}");
            return;
        }

        await using var spotlight = new DockSpotlight(session, NullLogger<DockSpotlight>.Instance);
        var diverged = new List<string>();
        spotlight.LensDiverged += diverged.Add;

        (await session.ToggleWindowDockAsync()).Should().Be(BrowserWindowState.Docked);
        var lens = await session.GetLensPageAsync();
        lens.Should().NotBeNull();

        // 1. Normal follow to page A.
        spotlight.RequestSync(new SpotlightTarget(urlA, $"{urlA}story-5", "Story 5"));
        await WaitForSpotlightAsync(lens!, "story-5");

        // 2. The USER drives the lens to a story on page B.
        var userUrl = $"{urlB}b-story-2";
        await lens!.GotoAsync(userUrl);

        // 3. App re-syncs the SAME page A target: the lens must NOT be yanked
        //    back, and adoption must be offered exactly once.
        spotlight.RequestSync(new SpotlightTarget(urlA, $"{urlA}story-6", "Story 6"));
        await Task.Delay(800);
        spotlight.RequestSync(new SpotlightTarget(urlA, $"{urlA}story-7", "Story 7"));
        await Task.Delay(800);
        lens.Url.Should().StartWith(userUrl, "the user's page must never be yanked away");
        diverged.Should().ContainSingle().Which.Should().StartWith(userUrl);

        // 4. The app moves to a NEW page — the new page wins; following resumes.
        spotlight.RequestSync(new SpotlightTarget(urlB, $"{urlB}b-story-9", "B-Story 9"));
        await WaitForSpotlightAsync(lens, "b-story-9");
        lens.Url.Should().StartWith(urlB);
    }

    private static async Task<SpotlightProbe> WaitForSpotlightAsync(Microsoft.Playwright.IPage page, string storySlug)
    {
        SpotlightProbe probe = default;
        await WaitUntilAsync(async () =>
        {
            probe = await ProbeAsync(page, storySlug);
            return probe.OverlayPresent && probe.AnchorInViewport && probe.OverlayWrapsAnchor;
        });
        return probe;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (await condition())
                {
                    return;
                }
            }
            catch (Microsoft.Playwright.PlaywrightException)
            {
                // Page mid-navigation — retry until the deadline.
            }

            await Task.Delay(150);
        }
    }

    /// <summary>
    /// Reads the live page's spotlight state: overlay presence, whether the
    /// anchor for the given story is inside the viewport, and whether the
    /// overlay's rect wraps the anchor's rect (small tolerance for padding).
    /// </summary>
    private static async Task<SpotlightProbe> ProbeAsync(Microsoft.Playwright.IPage page, string storySlug)
    {
        var raw = await page.EvaluateAsync<int[]>(
            """
            (slug) => {
                const o = document.getElementById('__wirecopy-spotlight');
                const a = Array.from(document.querySelectorAll('a[href]'))
                    .find(x => x.getAttribute('href').endsWith(slug)
                        && x.getBoundingClientRect().width > 0);
                if (!a) return [o ? 1 : 0, 0, 0];
                const r = a.getBoundingClientRect();
                const vh = window.innerHeight, vw = window.innerWidth;
                const inView = r.width > 0 && r.height > 0
                    && r.top >= 0 && r.left >= 0 && r.bottom <= vh && r.right <= vw;
                let wraps = 0;
                if (o) {
                    const b = o.getBoundingClientRect();
                    const tol = 6;
                    wraps = (b.top <= r.top + tol && b.left <= r.left + tol
                        && b.bottom >= r.bottom - tol && b.right >= r.right - tol) ? 1 : 0;
                }
                return [o ? 1 : 0, inView ? 1 : 0, wraps];
            }
            """,
            storySlug);

        return new SpotlightProbe(raw[0] == 1, raw[1] == 1, raw[2] == 1);
    }

    private readonly record struct SpotlightProbe(bool OverlayPresent, bool AnchorInViewport, bool OverlayWrapsAnchor)
    {
        public override string ToString()
            => $"overlay={OverlayPresent} anchorInViewport={AnchorInViewport} wraps={OverlayWrapsAnchor}";
    }

    /// <summary>
    /// Localhost HTTP server with two pages: 'a' has 100 story links spanning
    /// many viewports (plus a hidden duplicate of story-77 to exercise the
    /// visible-candidate tie-break); 'b' is the follow-navigation destination.
    /// </summary>
    private sealed class TinySiteServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly int _port;

        public TinySiteServer()
        {
            // HttpListener can't bind port 0; probe for a free port instead.
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            _port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
            _listener.Start();
            _ = Task.Run(ServeLoopAsync);
        }

        public string UrlFor(string pageName) => $"http://127.0.0.1:{_port}/{pageName}/";

        public void Dispose()
        {
            _cts.Cancel();
            try
            {
                _listener.Stop();
                _listener.Close();
            }
            catch (ObjectDisposedException)
            {
                // Already torn down.
            }

            _cts.Dispose();
        }

        private static string BuildPageA()
        {
            var sb = new StringBuilder("<!DOCTYPE html><html><head><title>A</title></head><body>");
            sb.Append("<h1>Page A</h1>");

            // Hidden duplicate href: the spotlight must pick the laid-out anchor.
            sb.Append("<a href=\"/a/story-77\" style=\"display:none\">hidden duplicate</a>");
            for (var i = 1; i <= 100; i++)
            {
                sb.Append($"<div style=\"height:200px\"><a href=\"/a/story-{i}\">Story {i}</a></div>");
            }

            sb.Append("</body></html>");
            return sb.ToString();
        }

        private static string BuildPageB()
        {
            var sb = new StringBuilder("<!DOCTYPE html><html><head><title>B</title></head><body>");
            sb.Append("<h1>Page B</h1>");
            for (var i = 1; i <= 30; i++)
            {
                sb.Append($"<div style=\"height:200px\"><a href=\"/b/b-story-{i}\">B-Story {i}</a></div>");
            }

            sb.Append("</body></html>");
            return sb.ToString();
        }

        private async Task ServeLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await _listener.GetContextAsync();
                }
                catch (Exception)
                {
                    return; // listener stopped
                }

                var path = ctx.Request.Url?.AbsolutePath ?? "/";
                var html = path.StartsWith("/b", StringComparison.Ordinal) ? BuildPageB() : BuildPageA();
                var bytes = Encoding.UTF8.GetBytes(html);
                try
                {
                    ctx.Response.ContentType = "text/html; charset=utf-8";
                    ctx.Response.ContentLength64 = bytes.Length;
                    await ctx.Response.OutputStream.WriteAsync(bytes, _cts.Token);
                    ctx.Response.Close();
                }
                catch (Exception)
                {
                    // Client went away mid-response; keep serving.
                }
            }
        }
    }
}
