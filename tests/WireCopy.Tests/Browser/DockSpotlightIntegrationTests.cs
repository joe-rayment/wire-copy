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

        using var queue = new PageAccessQueue(session, NullLogger<PageAccessQueue>.Instance);
        await using var spotlight = new DockSpotlight(session, queue, NullLogger<DockSpotlight>.Instance);

        await page.GotoAsync(urlA);
        (await session.ToggleWindowDockAsync()).Should().Be(BrowserWindowState.Docked);
        session.IsDocked.Should().BeTrue();

        // --- 1. Highlight a story far below the fold: overlay appears AND the
        //        anchor is scrolled into the viewport (off-screen = failure). ---
        spotlight.RequestSync(new SpotlightTarget(urlA, $"{urlA}story-77", "Story 77"));
        var probe77 = await WaitForSpotlightAsync(page, "story-77");
        _out.WriteLine($"story-77 probe: {probe77}");
        probe77.OverlayPresent.Should().BeTrue("the spotlight overlay must exist");
        probe77.AnchorInViewport.Should().BeTrue(
            "a highlighted story that is not visible on screen is the feature's defined failure");
        probe77.OverlayWrapsAnchor.Should().BeTrue("the overlay must wrap the selected anchor");

        // --- 2. Move the selection: the overlay follows and stays visible. ---
        spotlight.RequestSync(new SpotlightTarget(urlA, $"{urlA}story-5", "Story 5"));
        var probe5 = await WaitForSpotlightAsync(page, "story-5");
        _out.WriteLine($"story-5 probe : {probe5}");
        probe5.AnchorInViewport.Should().BeTrue();
        probe5.OverlayWrapsAnchor.Should().BeTrue();

        // --- 3. Follow-navigation: the TUI shows page B (cache hit — the live
        //        browser was never navigated), the spotlight navigates it. ---
        spotlight.RequestSync(new SpotlightTarget(urlB, $"{urlB}b-story-3", "B-Story 3"));
        var probeB = await WaitForSpotlightAsync(page, "b-story-3");
        _out.WriteLine($"b-story-3 probe: {probeB}, live URL: {page.Url}");
        page.Url.Should().StartWith(urlB, "the live page must follow the TUI to page B");
        probeB.AnchorInViewport.Should().BeTrue();
        probeB.OverlayWrapsAnchor.Should().BeTrue();

        // --- 4. Clear removes the overlay. ---
        spotlight.RequestClear();
        await WaitUntilAsync(async () => !(await ProbeAsync(page, "b-story-3")).OverlayPresent);
        (await ProbeAsync(page, "b-story-3")).OverlayPresent.Should().BeFalse();
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

        using var queue = new PageAccessQueue(session, NullLogger<PageAccessQueue>.Instance);
        await using var spotlight = new DockSpotlight(session, queue, NullLogger<DockSpotlight>.Instance);

        await page.GotoAsync(urlA);
        (await session.ToggleWindowDockAsync()).Should().Be(BrowserWindowState.Docked);

        // Light up a story on page A so there is a stale overlay for the follow to clear.
        spotlight.RequestSync(new SpotlightTarget(urlA, $"{urlA}story-5", "Story 5"));
        await WaitForSpotlightAsync(page, "story-5");

        // workspace-nqqs: reader view emits a follow-only target (page url, no anchor). The
        // live window must FOLLOW to page B (the article the terminal is now reading — a
        // cache hit never navigated the live window) AND drop the stale highlight, because
        // in reader view the page itself is the content so nothing should be boxed.
        spotlight.RequestSync(new SpotlightTarget(urlB, urlB, string.Empty, FollowPageOnly: true));
        await WaitUntilAsync(async () =>
            page.Url.StartsWith(urlB, StringComparison.Ordinal)
            && !(await ProbeAsync(page, "b-story-3")).OverlayPresent);

        page.Url.Should().StartWith(urlB, "the live page must follow the reader view to the article being read");
        (await ProbeAsync(page, "b-story-3")).OverlayPresent.Should().BeFalse(
            "follow-only reader view draws no highlight box — the page itself is the content");
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
