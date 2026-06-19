// G5 verification gate for the browser-hosted shell. Drives a full session with a real headless
// Chromium against the running WireCopy.Web and ASSERTS the user-visible behaviour, exiting non-zero
// on any failure. This is the real-world gate that replaces sampled "laws": it checks exactly what a
// user sees in the one tab.
//
//   1. the real TUI renders in xterm.js;
//   2. driving the TUI (o = go to url) makes the web pane follow-navigate to that page (the API's
//      own browser, streamed in) — non-blank pixels;
//   3. a click forwarded into the pane visibly changes the live page (the pane region differs).
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
var page = await browser.NewPageAsync(new BrowserNewPageOptions
{
    ViewportSize = new ViewportSize { Width = 1400, Height = 900 },
});

Console.WriteLine($"Gate: {baseUrl}");
await page.GotoAsync(baseUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 30000 });
await page.WaitForTimeoutAsync(15000); // TUI warmup + pane bridge connect

// 1) TUI renders.
var termText = await page.EvaluateAsync<string>(
    "() => document.querySelector('.xterm-rows') ? document.querySelector('.xterm-rows').innerText : ''");
Check(termText.Contains("Wire Copy") || termText.Contains("Go to URL") || termText.Contains("READING LIST"),
    "real TUI renders in xterm.js");

// 2) Drive the TUI to navigate; the pane should follow.
await page.ClickAsync("#term");
await page.WaitForTimeoutAsync(400);
await page.Keyboard.PressAsync("o");
await page.WaitForTimeoutAsync(700);
await page.Keyboard.TypeAsync(navUrl, new KeyboardTypeOptions { Delay = 15 });
await page.Keyboard.PressAsync("Enter");
await page.WaitForTimeoutAsync(9000);

var img = page.Locator("#screencast");
var paneBefore = await img.ScreenshotAsync();
await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(outDir, "gate-1-followed.png") });
Check(paneBefore.Length > 2000, "web pane shows a streamed page (non-blank)");

// 3) Forward a click onto the toggle button (page coords 50,300 / 220x60) and assert the pane changes.
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

Console.WriteLine();
if (failures.Count == 0)
{
    Console.WriteLine("GATE: PASS");
    return 0;
}

Console.WriteLine($"GATE: FAIL ({failures.Count}): {string.Join("; ", failures)}");
return 1;
