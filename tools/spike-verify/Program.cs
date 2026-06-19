// G5 verification gate for the browser-hosted shell. Drives a full session with a real headless
// Chromium against the running WireCopy.Web and ASSERTS the user-visible behaviour, exiting non-zero
// on any failure. This is the real-world gate that replaces sampled "laws": it checks exactly what a
// user sees in the one tab.
//
//   1. the real TUI renders in xterm.js;
//   2. the web pane is COLLAPSED at the launcher (never-empty law — nothing to show yet);
//   3. driving the TUI (o = go to url) REVEALS the pane in live mode and follow-navigates the API's
//      own browser, streamed in (non-blank pixels);
//   4. a click forwarded into the pane visibly changes the live page (the pane region differs);
//   5. F9 hides and re-reveals the pane;
//   6. a second tab streams its own page concurrently (per-tab Chromium profile — no SingletonLock).
//
// NOTE: page.Evaluate runs in an ISOLATED world (the stealth engine), which shares the DOM but NOT the
// page's JS globals — so all assertions read DOM state (classes / computed style), never window.__wc*.
using Microsoft.Playwright;

var baseUrl = Environment.GetEnvironmentVariable("WIRECOPY_WEB_URL") ?? "http://127.0.0.1:5099";
var outDir = Environment.GetEnvironmentVariable("SPIKE_OUT") ?? "/tmp";
var navUrl = Environment.GetEnvironmentVariable("WIRECOPY_TUI_NAV") ?? $"{baseUrl}/testpage.html";
var exe = Environment.GetEnvironmentVariable("WIRECOPY_CHROMIUM")
    ?? "/opt/pw-browsers/chromium-1194/chrome-linux/chrome";

var failures = new List<string>();
void Check(bool ok, string label)
{
    Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {label}");
    if (!ok)
    {
        failures.Add(label);
    }
}

using var pw = await Playwright.CreateAsync();
await using var browser = await pw.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
{
    Headless = true,
    ExecutablePath = File.Exists(exe) ? exe : null,
    Args = new[] { "--no-sandbox", "--disable-gpu" },
});

async Task<IPage> OpenTabAsync()
{
    var p = await browser.NewPageAsync(new BrowserNewPageOptions
    {
        ViewportSize = new ViewportSize { Width = 1400, Height = 900 },
    });
    await p.GotoAsync(baseUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 30000 });
    await p.WaitForTimeoutAsync(15000); // TUI warmup + pane bridge connect
    return p;
}

// DOM-state probes (isolated world → read the shared DOM, not page globals).
Task<bool> PaneHiddenAsync(IPage p) => p.EvaluateAsync<bool>(
    "() => { const e=document.querySelector('#web-pane'); return !e || e.classList.contains('hidden'); }");
Task<bool> ScreencastVisibleAsync(IPage p) => p.EvaluateAsync<bool>(
    "() => { const i=document.querySelector('#screencast'); return !!i && getComputedStyle(i).display !== 'none' && i.offsetParent !== null; }");

// Drives the TUI's "go to URL" (o) command to navigate the streamed pane.
async Task NavigateTuiAsync(IPage p, string url)
{
    await p.ClickAsync("#term");
    await p.WaitForTimeoutAsync(400);
    await p.Keyboard.PressAsync("o");
    await p.WaitForTimeoutAsync(700);
    await p.Keyboard.TypeAsync(url, new KeyboardTypeOptions { Delay = 15 });
    await p.Keyboard.PressAsync("Enter");
    await p.WaitForTimeoutAsync(9000);
}

Console.WriteLine($"Gate: {baseUrl}");
var page = await OpenTabAsync();

// 1) TUI renders.
var termText = await page.EvaluateAsync<string>(
    "() => document.querySelector('.xterm-rows') ? document.querySelector('.xterm-rows').innerText : ''");
Check(termText.Contains("Wire Copy") || termText.Contains("Go to URL") || termText.Contains("READING LIST"),
    "real TUI renders in xterm.js");

// 2) Never-empty law: at the launcher the pane is collapsed (no content to show).
Check(await PaneHiddenAsync(page), "web pane is collapsed at the launcher (never-empty)");

// 3) Drive the TUI to navigate; the pane should reveal in live mode and follow.
await NavigateTuiAsync(page, navUrl);
Check(!await PaneHiddenAsync(page) && await ScreencastVisibleAsync(page),
    "web pane reveals in live mode after navigation");

var img = page.Locator("#screencast");
var paneBefore = await img.ScreenshotAsync();
await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(outDir, "gate-1-followed.png") });
Check(paneBefore.Length > 2000, "web pane shows a streamed page (non-blank)");

// 4) Forward a click into the pane and assert the live page changes.
var geomJson = await page.EvaluateAsync<string>(
    "() => { const i=document.querySelector('#screencast'); const r=i.getBoundingClientRect();" +
    " return JSON.stringify({x:r.x,y:r.y,w:r.width,h:r.height,nw:i.naturalWidth,nh:i.naturalHeight}); }");
using var geom = System.Text.Json.JsonDocument.Parse(geomJson);
var g = geom.RootElement;
double gx = g.GetProperty("x").GetDouble(), gy = g.GetProperty("y").GetDouble();
double gw = g.GetProperty("w").GetDouble(), gh = g.GetProperty("h").GetDouble();
double nw = g.GetProperty("nw").GetDouble(), nh = g.GetProperty("nh").GetDouble();
if (nw > 0 && nh > 0)
{
    await page.Mouse.ClickAsync((float)(gx + (160.0 * (gw / nw))), (float)(gy + (330.0 * (gh / nh))));
    await page.WaitForTimeoutAsync(1500);
}

var paneAfter = await img.ScreenshotAsync();
await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(outDir, "gate-2-clicked.png") });
Check(!paneBefore.AsSpan().SequenceEqual(paneAfter), "forwarded click visibly changes the live page");

// 5) F9 hides and re-reveals the pane (real key press → the SPA's window keydown handler). Move focus
// off the terminal first (xterm swallows keys); the status span is inert.
await page.ClickAsync("#web-status");
await page.Keyboard.PressAsync("F9");
await page.WaitForTimeoutAsync(500);
var hiddenAfterF9 = await PaneHiddenAsync(page);
await page.Keyboard.PressAsync("F9");
await page.WaitForTimeoutAsync(500);
var shownAfterF9 = !await PaneHiddenAsync(page);
Check(hiddenAfterF9 && shownAfterF9, "F9 hides and re-reveals the web pane");

// 6) Reader view → SNAPSHOT mode: navigating to an article auto-enters reader view (the TUI does this
// for Article-classified pages), so the pane should swap the screencast for our clean HTML in the
// iframe, carrying the article text.
await NavigateTuiAsync(page, $"{baseUrl}/article.html");
await page.WaitForTimeoutAsync(6000); // classification + reader extraction + snapshot push
var iframeVisible = await page.EvaluateAsync<bool>(
    "() => { const f=document.querySelector('#web-iframe'); return !!f && getComputedStyle(f).display !== 'none'; }");
var snapshotText = await page.EvaluateAsync<string>(
    "() => { const f=document.querySelector('#web-iframe'); try { return (f && f.contentDocument && f.contentDocument.body) ? f.contentDocument.body.innerText : ''; } catch { return ''; } }");
await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(outDir, "gate-3-snapshot.png") });
Check(iframeVisible && snapshotText.Contains("WIRECOPY_SNAPSHOT_MARKER"),
    "reader view renders a crisp HTML snapshot in the iframe");

// 7) A second concurrent tab gets its own API child + Chromium profile and streams independently.
var page2 = await OpenTabAsync();
await NavigateTuiAsync(page2, navUrl);
var pane2 = await page2.Locator("#screencast").ScreenshotAsync();
await page2.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(outDir, "gate-3-second-tab.png") });
Check(pane2.Length > 2000 && !await PaneHiddenAsync(page2),
    "second concurrent tab streams its own page (per-tab profile)");

Console.WriteLine();
if (failures.Count == 0)
{
    Console.WriteLine("GATE: PASS");
    return 0;
}

Console.WriteLine($"GATE: FAIL ({failures.Count}): {string.Join("; ", failures)}");
return 1;
