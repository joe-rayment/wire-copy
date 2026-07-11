// Proves the app's existing browser-driving stack (Patchright .NET) can adopt the
// Electron shell's embedded page pane over CDP — the integration seam for the real plan.
using Microsoft.Playwright;

var port = args.Length > 0 ? args[0] : "9333";
using var pw = await Playwright.CreateAsync();
IBrowser browser = await pw.Chromium.ConnectOverCDPAsync($"http://127.0.0.1:{port}/");
Console.Error.WriteLine($"[probe] connected; contexts={browser.Contexts.Count}");

IPage? page = null;
foreach (var ctx in browser.Contexts)
{
    foreach (var p in ctx.Pages)
    {
        Console.Error.WriteLine($"[probe] page: {p.Url}");
        if (p.Url.Contains("thief") || p.Url.Contains("npr")) page = p;
    }
}
if (page is null) { Console.WriteLine("FAIL=no-target"); return 3; }

await page.GotoAsync("https://text.npr.org/", new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 });
var title = await page.TitleAsync();
var math = await page.EvaluateAsync<int>("21*2");
Console.WriteLine($"TITLE={title}|MATH={math}");
return title.Contains("NPR", StringComparison.OrdinalIgnoreCase) && math == 42 ? 0 : 2;
