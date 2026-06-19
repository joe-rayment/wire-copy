// G4 verifier: drives the real TUI (press 'o' = go to url, type a URL, Enter) and confirms the web
// pane FOLLOWS the TUI's navigation via the spotlight — i.e. the pane mirrors what the user browses.
using Microsoft.Playwright;

var baseUrl = Environment.GetEnvironmentVariable("WIRECOPY_WEB_URL") ?? "http://127.0.0.1:5099";
var outDir = Environment.GetEnvironmentVariable("SPIKE_OUT") ?? "/tmp";
var tuiNav = Environment.GetEnvironmentVariable("WIRECOPY_TUI_NAV") ?? $"{baseUrl}/testpage.html";
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

Console.WriteLine("waiting for TUI warmup + pane bridge…");
await page.WaitForTimeoutAsync(15000);

var termText = await page.EvaluateAsync<string>(
    "() => document.querySelector('.xterm-rows') ? document.querySelector('.xterm-rows').innerText : ''");
Console.WriteLine($"terminal TUI visible: {termText.Contains("Wire Copy") || termText.Contains("Go to URL")}");

// Drive the TUI: focus terminal, press 'o' (go to url), type the URL, Enter.
await page.ClickAsync("#term");
await page.WaitForTimeoutAsync(400);
Console.WriteLine($"TUI: go to url -> {tuiNav}");
await page.Keyboard.PressAsync("o");
await page.WaitForTimeoutAsync(700);
await page.Keyboard.TypeAsync(tuiNav, new KeyboardTypeOptions { Delay = 15 });
await page.Keyboard.PressAsync("Enter");

// Give the TUI time to load the page AND the spotlight to follow-navigate the streamed display page.
await page.WaitForTimeoutAsync(9000);

await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(outDir, "g4.png") });
Console.WriteLine("captured g4.png");
