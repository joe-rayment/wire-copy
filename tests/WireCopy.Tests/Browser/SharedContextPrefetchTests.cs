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
/// workspace-wo4q: background prefetch pages are TABS of the user's ONE real
/// browser. Proves (1) one shared context/cookie jar — a cookie set in the
/// visible browser flows to a background-tab request with no sync step; and
/// (2) tab etiquette — creating and navigating a background tab never steals
/// the active (lens) tab or moves the window.
/// </summary>
[Trait("Category", "Integration")]
[Collection(WireCopy.Tests.HeadedBrowserSerialCollection.Name)]
public class SharedContextPrefetchTests
{
    private readonly ITestOutputHelper _out;

    public SharedContextPrefetchTests(ITestOutputHelper output) => _out = output;

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task BackgroundTab_SharesTheOneCookieJar()
    {
        Skip.If(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")),
            "Headed browser requires an X display — run under xvfb-run.");

        using var echo = new CookieEchoServer();
        var config = Options.Create(new BrowserConfiguration { Visibility = BrowserVisibility.Visible, Sidecar = false });
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

        // A login performed in the user's visible browser…
        await page.Context.AddCookiesAsync([new Microsoft.Playwright.Cookie
        {
            Name = "wc_session",
            Value = "logged-in-123",
            Url = echo.BaseUrl,
        }]);

        // …must be carried by a background-tab prefetch with NO sync step.
        var bg = await session.CreateBackgroundPageAsync();
        bg.Should().NotBeNull();
        bg!.Context.Should().BeSameAs(page.Context, "prefetch shares the ONE context");

        await bg.GotoAsync(echo.BaseUrl);
        var body = await bg.InnerTextAsync("body");
        _out.WriteLine($"echo server saw: {body}");
        body.Should().Contain("wc_session=logged-in-123",
            "cookies from the visible browser must reach prefetch requests");

        await session.CloseBackgroundPageAsync(bg);
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task BackgroundTab_NeverStealsTheLensOrMovesTheWindow()
    {
        Skip.If(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")),
            "Headed browser requires an X display — run under xvfb-run.");

        using var echo = new CookieEchoServer();
        var config = Options.Create(new BrowserConfiguration { Visibility = BrowserVisibility.Visible, Sidecar = false });
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

        (await session.ToggleWindowDockAsync()).Should().Be(BrowserWindowState.Docked);
        var lens = await session.GetLensPageAsync();
        lens.Should().NotBeNull();
        var boundsBefore = await ReadBoundsAsync(lens!);

        // Track every moment the lens stops being the active tab. (Asserting the
        // bg tab's own visibilityState is unreliable on a bare Xvfb — a never-
        // activated tab can report 'visible' — but a genuine tab steal ALWAYS
        // fires visibilitychange/hidden on the tab it displaced.)
        await lens!.EvaluateAsync(
            "() => { window.__wcHiddenCount = 0;" +
            " document.addEventListener('visibilitychange'," +
            " () => { if (document.hidden) window.__wcHiddenCount++; }); }");

        var bg = await session.CreateBackgroundPageAsync();
        bg.Should().NotBeNull();
        bg!.Context.Should().BeSameAs(lens.Context, "one window, one context");
        (await WindowIdAsync(bg)).Should().Be(await WindowIdAsync(lens), "prefetch is a TAB, not a second window");
        await bg.GotoAsync(echo.BaseUrl);
        await bg.GotoAsync(echo.BaseUrl + "second");
        await Task.Delay(300);

        // The lens never lost the active tab through creation + navigations…
        (await lens.EvaluateAsync<string>("() => document.visibilityState"))
            .Should().Be("visible", "the lens must stay the active tab");
        (await lens.EvaluateAsync<int>("() => window.__wcHiddenCount"))
            .Should().Be(0, "prefetch must never displace the lens, even transiently");

        // …and the window has not moved or changed state.
        var boundsAfter = await ReadBoundsAsync(lens);
        _out.WriteLine($"bounds before={boundsBefore} after={boundsAfter}");
        boundsAfter.Should().Be(boundsBefore, "prefetch must never move/raise the window");

        await session.CloseBackgroundPageAsync(bg);
    }

    private static async Task<int> WindowIdAsync(Microsoft.Playwright.IPage page)
    {
        var cdp = await page.Context.NewCDPSessionAsync(page);
        var info = await cdp.SendAsync("Browser.getWindowForTarget")
            ?? throw new InvalidOperationException("no payload");
        return info.GetProperty("windowId").GetInt32();
    }

    private static async Task<(int Left, int Top, int Width, int Height, string State)> ReadBoundsAsync(
        Microsoft.Playwright.IPage page)
    {
        var cdp = await page.Context.NewCDPSessionAsync(page);
        var info = await cdp.SendAsync("Browser.getWindowForTarget")
            ?? throw new InvalidOperationException("getWindowForTarget returned no payload");
        var b = info.GetProperty("bounds");
        return (
            b.GetProperty("left").GetInt32(),
            b.GetProperty("top").GetInt32(),
            b.GetProperty("width").GetInt32(),
            b.GetProperty("height").GetInt32(),
            b.GetProperty("windowState").GetString() ?? "unknown");
    }

    /// <summary>Localhost server echoing the request's Cookie header.</summary>
    private sealed class CookieEchoServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new();

        public CookieEchoServer()
        {
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            BaseUrl = $"http://127.0.0.1:{port}/";
            _listener = new HttpListener();
            _listener.Prefixes.Add(BaseUrl);
            _listener.Start();
            _ = Task.Run(ServeLoopAsync);
        }

        public string BaseUrl { get; }

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
            }

            _cts.Dispose();
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
                    return;
                }

                var cookies = ctx.Request.Headers["Cookie"] ?? "(none)";
                var bytes = Encoding.UTF8.GetBytes(
                    $"<!DOCTYPE html><html><body>cookies: {cookies}</body></html>");
                try
                {
                    ctx.Response.ContentType = "text/html; charset=utf-8";
                    ctx.Response.ContentLength64 = bytes.Length;
                    await ctx.Response.OutputStream.WriteAsync(bytes, _cts.Token);
                    ctx.Response.Close();
                }
                catch (Exception)
                {
                }
            }
        }
    }
}
