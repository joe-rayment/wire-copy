// workspace-a2ml — End-to-end real-experience gate for the Wire Copy extension (host-browser-as-
// renderer). Launches a HEADFUL Chromium (under Xvfb in CI) with the unpacked extension loaded, opens
// a REAL bot-heavy site as the tab's own top-level page, and asserts what the user actually gets:
//
//   1. SINGLE WINDOW: the extension never opens a second tab/window (page count stays 1).
//   2. NO BOT BLOCK: the real site renders (no Cloudflare "Just a moment" / "Attention Required"),
//      because the user's own browser is the renderer.
//   3. OVERLAY DOCKS: the Wire Copy TUI overlay (an extension-owned iframe, not a site frame) injects.
//   4. DOM CAPTURE: the rendered DOM is large and real (the content script's source material).
//   5. BACKEND EXTRACTION: the WireCopy.API child, fed the extension's DOM over /ws/ext, extracts a
//      non-trivial link list (asserted from the child's log — "Page loaded: ... N links").
//
// Exits non-zero on any failure. Screenshots land in $VERIFY_OUT (default /tmp/verify-ext).
using Microsoft.Playwright;

var baseUrl = Environment.GetEnvironmentVariable("WIRECOPY_WEB_URL") ?? "http://127.0.0.1:5099";
// "section" (default): a real bot-heavy section page — proves bot-block defeat + link extraction + the
// spotlight drive verb. "reader": a plain article — proves reader-view auto-switch + the scrollTo drive
// verb that keeps the real page following the article (workspace-blg5.1).
var mode = (Environment.GetEnvironmentVariable("VERIFY_MODE") ?? "section").Trim().ToLowerInvariant();
var site = Environment.GetEnvironmentVariable("VERIFY_SITE") ?? "https://www.macleans.ca/";
var extDir = Environment.GetEnvironmentVariable("VERIFY_EXT_DIR") ?? Path.GetFullPath("extension");
var outDir = Environment.GetEnvironmentVariable("VERIFY_OUT") ?? "/tmp/verify-ext";
var logGlobDir = Environment.GetEnvironmentVariable("VERIFY_LOG_DIR") ?? "logs";
var exe = Environment.GetEnvironmentVariable("WIRECOPY_CHROMIUM")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".cache/ms-playwright/chromium-1223/chrome-linux/chrome");
var profileDir = Environment.GetEnvironmentVariable("VERIFY_PROFILE") ?? "/tmp/verify-ext-profile";

Directory.CreateDirectory(outDir);
if (Directory.Exists(profileDir))
{
    try { Directory.Delete(profileDir, true); } catch { /* best effort */ }
}

var failures = new List<string>();
void Check(bool ok, string label)
{
    Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {label}");
    if (!ok)
    {
        failures.Add(label);
    }
}

Console.WriteLine($"== verify-ext: backend={baseUrl} mode={mode} site={site} ext={extDir} ==");
Console.WriteLine($"   chromium={exe} (exists={File.Exists(exe)})");

// "webshell" mode verifies the LEGACY browser-hosted split-pane shell (index.html) directly — no
// extension is loaded; we just drive the served SPA at the backend URL and assert the launcher.
var webshellMode = mode == "webshell";
var baseArgs = new List<string> { "--no-sandbox", "--disable-gpu", "--no-first-run", "--no-default-browser-check" };
if (!webshellMode)
{
    baseArgs.Add($"--disable-extensions-except={extDir}");
    baseArgs.Add($"--load-extension={extDir}");
}

using var pw = await Playwright.CreateAsync();
await using var context = await pw.Chromium.LaunchPersistentContextAsync(profileDir, new BrowserTypeLaunchPersistentContextOptions
{
    Headless = false, // extensions require a headful context; runs under Xvfb in CI
    ExecutablePath = File.Exists(exe) ? exe : null,
    ViewportSize = ViewportSize.NoViewport,
    Args = baseArgs.ToArray(),
});

// Point the extension at our backend before any page script reads it (extension modes only).
if (!webshellMode)
{
    try
    {
        await context.AddInitScriptAsync(
            $"try{{chrome.storage&&chrome.storage.local&&chrome.storage.local.set({{backend:'{baseUrl}'}});}}catch(e){{}}");
    }
    catch { /* init script is best-effort; default ws://127.0.0.1:5099 matches the gate anyway */ }
}

var page = context.Pages.Count > 0 ? context.Pages[0] : await context.NewPageAsync();
page.PageError += (_, e) => Console.WriteLine($"   [pageerror] {e}");
page.Console += (_, m) => { if (m.Type is "error" or "warning") Console.WriteLine($"   [console:{m.Type}] {m.Text}"); };

Console.WriteLine($"== navigate to {site} ==");
try
{
    await page.GotoAsync(site, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 45000 });
}
catch (Exception ex)
{
    Console.WriteLine($"   goto warning: {ex.Message}");
}

// Let the page render, the content script inject the overlay, and the backend child capture + extract.
await page.WaitForTimeoutAsync(18000);

try { await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(outDir, "ext-site.png") }); }
catch { /* screenshot best-effort */ }

// SINGLE WINDOW — no second tab/window opened (holds in every mode).
Check(context.Pages.Count == 1, $"single window (no 2nd tab) — pages={context.Pages.Count}");

if (webshellMode)
{
    // LEGACY WEB SHELL launcher (workspace-yqt5.2 / yqt5.3 / opb2): the SPA loaded at the backend URL
    // shows the launcher. Assert the never-empty law and the ASCII-art wordmark.

    // a) Pane COLLAPSED at the launcher — nothing to show yet (workspace-yqt5.2). Read the DOM, not the
    //    page's window globals: this gate's page.evaluate runs in an ISOLATED world that shares the DOM
    //    but not the page's JS globals. The pane carrying the 'hidden' class (.hidden => display:none)
    //    means it is collapsed; assert it STAYS collapsed (never revealed) across the settle window.
    var collapsed = false;
    var paneClass = string.Empty;
    for (var i = 0; i < 8; i++)
    {
        paneClass = await page.EvaluateAsync<string>("() => { const e = document.getElementById('web-pane'); return e ? e.className : '<none>'; }");
        collapsed = paneClass.Contains("hidden", StringComparison.Ordinal);
        if (!collapsed)
        {
            break; // revealed — fail fast
        }

        await page.WaitForTimeoutAsync(1000);
    }

    var paneDisplayed = await page.EvaluateAsync<bool>("() => { const e = document.getElementById('web-pane'); return !!e && getComputedStyle(e).display !== 'none'; }");
    Check(collapsed && !paneDisplayed, $"web pane collapsed at the launcher (never-empty law) — class='{paneClass}' displayed={paneDisplayed}");

    // b) Launcher shows the ASCII-art wordmark, not plain text (workspace-yqt5.3). With the pane hidden
    //    the terminal is full-width, so the block-glyph wordmark fits; '█' appears only in the art.
    var rows = string.Empty;
    for (var i = 0; i < 10; i++)
    {
        rows = await page.EvaluateAsync<string>("() => { const e = document.querySelector('.xterm-rows'); return e ? e.innerText : ''; }");
        if (rows.Contains('█'))
        {
            break;
        }

        await page.WaitForTimeoutAsync(1000);
    }

    Check(rows.Contains('█'), $"launcher shows the ASCII-art wordmark (block glyphs present) — rowsLen={rows.Length}");
}
else
{
    // OVERLAY DOCKS — the extension-owned TUI iframe injected (extension modes).
    var overlay = await page.QuerySelectorAsync("#wire-copy-overlay");
    Check(overlay != null, "Wire Copy overlay iframe injected");

    if (mode == "reader")
{
    // READER VIEW (workspace-blg5.1): the adopted page is a plain article, so the backend auto-switches
    // to reader view and drives the real page to FOLLOW the article via the scrollTo verb.

    // The scrollTo drive verb reached the live page. content.js records every drive command onto a shared
    // DOM attribute (its window globals live in an isolated world the gate can't read), so a "scrollTo"
    // entry proves the reader-follow path is WIRED end-to-end. This is ALSO the genuine reader-view proof:
    // SyncExtensionSpotlight drives scrollTo ONLY when viewMode == Readable, so the drive transitively
    // proves reader view was entered. Poll to absorb cold-start load timing.
    var drives = string.Empty;
    for (var i = 0; i < 12; i++)
    {
        drives = await page.EvaluateAsync<string>("() => document.documentElement.getAttribute('data-wirecopy-drives') || ''");
        if (drives.Contains("scrollTo", StringComparison.Ordinal))
        {
            break;
        }

        await page.WaitForTimeoutAsync(1500);
    }

    Check(drives.Contains("scrollTo", StringComparison.Ordinal),
        $"reader view drives the real page to follow via the scrollTo verb — drives=[{drives}]");

    // And the backend actually extracted readable content (the source material for reader view). By now
    // the scrollTo poll has given the child time to flush its "Page loaded: ... has readable" line, so
    // this one-shot check is no longer timing-fragile. ("has readable" proves readable content exists, not
    // the view switch itself — the scrollTo drive above is what proves reader view actually engaged.)
    Check(LogContains(logGlobDir, "has readable"),
        "backend extracted readable content — 'has readable' in log");
}
else
{
    // NO BOT BLOCK — the real site rendered (not a Cloudflare interstitial).
    var bodyText = await page.EvaluateAsync<string>("() => (document.body && document.body.innerText) || ''");
    var blocked = bodyText.Contains("Just a moment", StringComparison.OrdinalIgnoreCase)
        || bodyText.Contains("Attention Required", StringComparison.OrdinalIgnoreCase)
        || bodyText.Contains("Checking your browser", StringComparison.OrdinalIgnoreCase)
        || bodyText.Contains("Enable JavaScript and cookies to continue", StringComparison.OrdinalIgnoreCase);
    Check(!blocked && bodyText.Length > 800, $"no bot block — body {bodyText.Length} chars, blocked={blocked}");

    // DOM CAPTURE — the rendered DOM the content script ships is large and real.
    var htmlLen = await page.EvaluateAsync<int>("() => document.documentElement.outerHTML.length");
    var anchorCount = await page.EvaluateAsync<int>("() => document.querySelectorAll('a[href]').length");
    Check(htmlLen > 20000 && anchorCount > 20, $"rendered DOM real — {htmlLen} bytes, {anchorCount} anchors");

    // BACKEND EXTRACTION — the API child, fed the extension DOM over /ws/ext, extracted links.
    //    Require BOTH proof the EXTENSION supplied the DOM ("via extension") AND a non-trivial link tree.
    var extracted = FindExtractionInLogs(logGlobDir, out var linkLine, out var viaExtension);
    Check(extracted && viaExtension,
        $"backend extracted links from extension DOM — viaExtension={viaExtension}, {linkLine ?? "no nav-tree line found"}");

    // SPOTLIGHT — selecting a story in the TUI drives the extension to highlight it on the REAL page.
    //    On initial render the orchestrator auto-selects the first story, so the spotlight box
    //    (#__wire-copy-spotlight, drawn by content.js on the backend's highlight command over /ws/ext)
    //    should be present on the live page. This proves the full drive path is WIRED, not just defined.
    var spotlight = false;
    for (var i = 0; i < 8 && !spotlight; i++)
    {
        spotlight = await page.EvaluateAsync<bool>("() => !!document.getElementById('__wire-copy-spotlight')");
        if (!spotlight)
        {
            await page.WaitForTimeoutAsync(1500);
        }
    }

    Check(spotlight, "story-select highlights the live page via the extension (#__wire-copy-spotlight present)");
    }
}

await context.CloseAsync();

Console.WriteLine();
if (failures.Count == 0)
{
    Console.WriteLine($"GATE PASSED [{mode}] ({site}) — screenshots in {outDir}");
    return 0;
}

Console.WriteLine($"GATE FAILED [{mode}]: {failures.Count} check(s) — {string.Join("; ", failures)}");
return 1;

// True if any recent WireCopy.API child log contains the given needle (used for the reader-view check,
// e.g. "has readable" from the "Page loaded: <title> - N links, has readable" line).
static bool LogContains(string dir, string needle)
{
    try
    {
        if (!Directory.Exists(dir))
        {
            return false;
        }

        foreach (var log in Directory.GetFiles(dir, "wirecopy-*.log").OrderByDescending(File.GetLastWriteTimeUtc))
        {
            using var fs = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            if (sr.ReadToEnd().Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
    }
    catch
    {
        // best effort
    }

    return false;
}

// Scans the WireCopy.API child log for proof the extension supplied the DOM ("via extension") and a
// non-trivial link tree ("Built navigation tree with N links" or "Page loaded: ... N links", N>0).
static bool FindExtractionInLogs(string dir, out string? line, out bool viaExtension)
{
    line = null;
    viaExtension = false;
    try
    {
        if (!Directory.Exists(dir))
        {
            return false;
        }

        var logs = Directory.GetFiles(dir, "wirecopy-*.log")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToList();
        var found = false;
        foreach (var log in logs)
        {
            string text;
            using (var fs = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sr = new StreamReader(fs))
            {
                text = sr.ReadToEnd();
            }

            if (text.Contains("via extension", StringComparison.OrdinalIgnoreCase))
            {
                viaExtension = true;
            }

            foreach (var l in text.Split('\n').Reverse())
            {
                var marker = l.Contains("Built navigation tree with ", StringComparison.Ordinal)
                    ? "Built navigation tree with "
                    : (l.Contains("Page loaded:", StringComparison.Ordinal) && l.Contains("links", StringComparison.Ordinal)
                        ? " - "
                        : null);
                if (marker == null)
                {
                    continue;
                }

                var idx = l.IndexOf(marker, StringComparison.Ordinal);
                var rest = l[(idx + marker.Length)..];
                var num = new string(rest.TakeWhile(char.IsDigit).ToArray());
                if (int.TryParse(num, out var n) && n > 0)
                {
                    line = l.Trim();
                    found = true;
                    break;
                }
            }

            if (found)
            {
                break;
            }
        }

        return found;
    }
    catch
    {
        // best effort
        return false;
    }
}
