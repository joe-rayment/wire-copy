// G0 spike verifier: drives the WireCopy.Web tab with a real headless Chromium and
// screenshots it, proving terminal round-trip + interactive web-pane screencast in one tab.
using Microsoft.Playwright;

var baseUrl = Environment.GetEnvironmentVariable("WIRECOPY_WEB_URL") ?? "http://127.0.0.1:5099";
var outDir = Environment.GetEnvironmentVariable("SPIKE_OUT") ?? "/tmp";
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

var noType = Environment.GetEnvironmentVariable("SPIKE_NO_TYPE") == "1";
if (!noType)
{
    // 1) Terminal round-trip: focus the terminal and type a command the PTY/bash will echo.
    await page.ClickAsync("#term");
    await page.WaitForTimeoutAsync(500);
    await page.Keyboard.TypeAsync("echo WIRECOPY_TERMINAL_OK_42\n");
    await page.WaitForTimeoutAsync(800);
}
else
{
    // Real-TUI capture: allow warmup (headless chromium launch) + first render.
    await page.WaitForTimeoutAsync(13000);
}

// 2) Web pane: wait for the screencast to produce a frame.
try
{
    await page.WaitForFunctionAsync("() => window.__wcPaneReady && window.__wcPaneReady()",
        new PageWaitForFunctionOptions { Timeout = 20000 });
    Console.WriteLine("screencast ready");
}
catch (TimeoutException)
{
    Console.WriteLine("WARN: screencast not ready within timeout");
}

await page.WaitForTimeoutAsync(800);
await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(outDir, "spike-initial.png"), FullPage = false });
Console.WriteLine("captured spike-initial.png");

// 3) Forward a click onto the streamed page's toggle button (pinned at page coords 50,300 / 220x60).
//    Use a real trusted mouse click on the <img> so the page's own click handler maps it to page
//    space and forwards it over the web-pane websocket (Patchright isolates evaluate() from page
//    globals, so we cannot call window.__wcPaneClick directly).
var geomJson = await page.EvaluateAsync<string>(
    "() => { const i=document.querySelector('#screencast'); const r=i.getBoundingClientRect();" +
    " return JSON.stringify({x:r.x,y:r.y,w:r.width,h:r.height,nw:i.naturalWidth,nh:i.naturalHeight}); }");
using var geom = System.Text.Json.JsonDocument.Parse(geomJson);
var g = geom.RootElement;
double gx = g.GetProperty("x").GetDouble(), gy = g.GetProperty("y").GetDouble();
double gw = g.GetProperty("w").GetDouble(), gh = g.GetProperty("h").GetDouble();
double nw = g.GetProperty("nw").GetDouble(), nh = g.GetProperty("nh").GetDouble();
Console.WriteLine($"img rect=({gx},{gy}) {gw}x{gh} natural={nw}x{nh}");
var clientX = gx + (160.0 * (gw / nw));
var clientY = gy + (330.0 * (gh / nh));
await page.Mouse.ClickAsync((float)clientX, (float)clientY);
await page.WaitForTimeoutAsync(1200);
await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(outDir, "spike-final.png"), FullPage = false });
Console.WriteLine("captured spike-final.png");

// 4) Report whether the terminal text rendered (read xterm buffer text from the DOM).
var termText = await page.EvaluateAsync<string>(
    "() => document.querySelector('.xterm-rows') ? document.querySelector('.xterm-rows').innerText : ''");
var terminalOk = termText.Contains("WIRECOPY_TERMINAL_OK_42");
Console.WriteLine($"terminal echo visible: {terminalOk}");

Console.WriteLine(terminalOk ? "SPIKE-RESULT: terminal OK" : "SPIKE-RESULT: terminal MISSING");
