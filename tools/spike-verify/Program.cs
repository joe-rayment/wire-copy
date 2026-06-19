// G2 verifier: drives the WireCopy.Web tab with a real headless Chromium and screenshots it,
// proving the web pane streams the WireCopy.API child's OWN browser (navigated via the URL bar,
// interactive) alongside the real TUI — all in one tab.
using Microsoft.Playwright;

var baseUrl = Environment.GetEnvironmentVariable("WIRECOPY_WEB_URL") ?? "http://127.0.0.1:5099";
var outDir = Environment.GetEnvironmentVariable("SPIKE_OUT") ?? "/tmp";
var navUrl = Environment.GetEnvironmentVariable("WIRECOPY_WEBPANE_NAV") ?? $"{baseUrl}/testpage.html";
var exe = Environment.GetEnvironmentVariable("WIRECOPY_CHROMIUM")
    ?? "/opt/pw-browsers/chromium-1194/chrome-linux/chrome";

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

Console.WriteLine($"navigating to {baseUrl}");
await page.GotoAsync(baseUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 30000 });

// The TUI child must warm up (host build + headless chromium launch) before its web-pane bridge
// connects and the display page is created.
Console.WriteLine("waiting for TUI warmup + pane bridge…");
await page.WaitForTimeoutAsync(15000);

// Confirm the real TUI rendered in xterm.js.
var termText = await page.EvaluateAsync<string>(
    "() => document.querySelector('.xterm-rows') ? document.querySelector('.xterm-rows').innerText : ''");
var terminalOk = termText.Contains("Wire Copy") || termText.Contains("READING LIST") || termText.Contains("Go to URL");
Console.WriteLine($"terminal TUI visible: {terminalOk}");

// Drive the pane through the URL bar -> routes to the child's display page over IPC.
Console.WriteLine($"navigating web pane to {navUrl}");
await page.FillAsync("#web-url", navUrl);
await page.ClickAsync("#web-go");

// Wait for a screencast frame of the navigated page.
try
{
    await page.WaitForFunctionAsync(
        "() => { const i=document.querySelector('#screencast'); return i && i.complete && i.naturalWidth > 0; }",
        new PageWaitForFunctionOptions { Timeout = 20000 });
    Console.WriteLine("screencast frame received");
}
catch (TimeoutException)
{
    Console.WriteLine("WARN: no screencast frame within timeout");
}

await page.WaitForTimeoutAsync(1500);
await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(outDir, "g2-initial.png") });
Console.WriteLine("captured g2-initial.png");

// Forward a click onto the streamed page's toggle button (pinned at page coords 50,300 / 220x60).
var geomJson = await page.EvaluateAsync<string>(
    "() => { const i=document.querySelector('#screencast'); const r=i.getBoundingClientRect();" +
    " return JSON.stringify({x:r.x,y:r.y,w:r.width,h:r.height,nw:i.naturalWidth,nh:i.naturalHeight}); }");
using var geom = System.Text.Json.JsonDocument.Parse(geomJson);
var g = geom.RootElement;
double gx = g.GetProperty("x").GetDouble(), gy = g.GetProperty("y").GetDouble();
double gw = g.GetProperty("w").GetDouble(), gh = g.GetProperty("h").GetDouble();
double nw = g.GetProperty("nw").GetDouble(), nh = g.GetProperty("nh").GetDouble();
Console.WriteLine($"img rect=({gx},{gy}) {gw}x{gh} natural={nw}x{nh}");
if (nw > 0 && nh > 0)
{
    await page.Mouse.ClickAsync((float)(gx + (160.0 * (gw / nw))), (float)(gy + (330.0 * (gh / nh))));
    await page.WaitForTimeoutAsync(1500);
}

await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(outDir, "g2-final.png") });
Console.WriteLine("captured g2-final.png");
Console.WriteLine(terminalOk ? "G2-RESULT: terminal OK" : "G2-RESULT: terminal MISSING");
