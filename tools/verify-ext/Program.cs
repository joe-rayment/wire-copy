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
// "navigate" (workspace-blg5.6): reproduce the REAL user flow the other modes never exercise — the tab
// starts on the backend placeholder and we drive the OVERLAY's go-to-url to navigate, asserting the
// tab's TOP-LEVEL url actually changes (i.e. chrome.tabs.update fired) and content renders.
var navigateMode = mode == "navigate";
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

// CARDINAL (workspace-blg5.3): "single window" also requires that the BACKEND launched no server-side
// browser. context.Pages above sees only the DRIVER's context; a browser the WireCopy backend launches
// is a SEPARATE process the driver is blind to — and that blindness is exactly how the stray-window
// regression (workspace-blg5.2) shipped GREEN. The backend logs "Creating new Playwright browser session"
// the instant it launches, so scan the child log for it. Extension modes only — the legacy webshell
// launches a server-side browser BY DESIGN, so this invariant does not apply there.
if (!webshellMode)
{
    var backendLaunchedBrowser =
        LogContains(logGlobDir, "Creating new Playwright browser session")
        || LogContains(logGlobDir, "Ensuring Playwright browsers are installed");
    Check(!backendLaunchedBrowser,
        "backend launched NO server-side browser (extension mode is single-window by construction) — see workspace-blg5.2");
}

if (navigateMode)
{
    // workspace-blg5.6: drive the docked TUI to navigate, the way the user does — type a URL into the
    // overlay's go-to-url and press Enter — then assert the tab's TOP-LEVEL url actually changed off the
    // placeholder. The prior gate used page.GotoAsync(site), bypassing exactly this overlay->/ws/ext->
    // chrome.tabs.update path, which is why the "can't load webpages" bug shipped green.
    var navTarget = Environment.GetEnvironmentVariable("VERIFY_NAV_TARGET") ?? "macleans.ca";
    var startUrl = page.Url;
    Console.WriteLine($"== drive overlay go-to-url: type '{navTarget}' + Enter (start={startUrl}) ==");

    var overlayFrame = page.Frames.FirstOrDefault(f => f.Url.Contains("overlay.html", StringComparison.Ordinal));
    Check(overlayFrame != null, $"overlay TUI frame present (frames={page.Frames.Count})");

    // workspace-blg5.7: on the launcher (no live page) the overlay must be FULL-WIDTH — no placeholder
    // bleed-through, launcher not cramped into a thin strip.
    var vwLauncher = await page.EvaluateAsync<int>("() => window.innerWidth");
    var ovLauncher = await page.QuerySelectorAsync("#wire-copy-overlay");
    var boxLauncher = ovLauncher != null ? await ovLauncher.BoundingBoxAsync() : null;
    Check(boxLauncher != null && boxLauncher.Width >= vwLauncher * 0.9,
        $"launcher: overlay is full-width (w={boxLauncher?.Width}, vw={vwLauncher})");

    try { await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(outDir, "ext-launcher.png") }); }
    catch { /* best effort */ }

    // workspace-blg5.7/.9: capture the launcher's xterm column count (full-width) so we can later assert
    // the TUI actually REFLOWED (cols shrank) when the overlay docks to the split — not just that the
    // iframe got narrower while the terminal kept its wide column count (the clipped-TUI bug).
    var launcherCols = 0;
    if (overlayFrame != null)
    {
        try { launcherCols = await overlayFrame.EvaluateAsync<int>("() => parseInt(document.documentElement.getAttribute('data-wc-cols') || '0', 10)"); }
        catch { /* frame not ready */ }
    }

    Console.WriteLine($"   [cols] launcher xterm cols={launcherCols}");

    if (overlayFrame != null)
    {
        // Focus the xterm; the launcher routes the first printable char into the go-to-url field, Enter
        // submits (HandleGoToUrl prepends https://). Click the terminal, then type into the focused frame.
        try { await overlayFrame.ClickAsync(".xterm", new FrameClickOptions { Timeout = 5000 }); }
        catch { try { await overlayFrame.ClickAsync("body", new FrameClickOptions { Timeout = 5000 }); } catch { /* best effort */ } }
        await page.WaitForTimeoutAsync(500);
        var via = (Environment.GetEnvironmentVariable("VERIFY_NAV_VIA") ?? "gotourl").Trim().ToLowerInvariant();
        if (via == "bookmark")
        {
            // Open the selected launcher bookmark (Enter). This exercises LauncherCommandHandler:176
            // which passes the RAW bookmark.Url — the candidate bug for "can't load webpages".
            Console.WriteLine("   [drive] opening selected bookmark via Enter");
            await page.Keyboard.PressAsync("Enter");
        }
        else
        {
            // Launcher footer: "[o] go to url" — press 'o' to open the URL bar, THEN type. (Plain
            // printable chars don't open go-to-url; 'a' would open Add Bookmark, etc.)
            await page.Keyboard.PressAsync("o");
            await page.WaitForTimeoutAsync(500);
            await page.Keyboard.TypeAsync(navTarget, new KeyboardTypeOptions { Delay = 40 });
            await page.WaitForTimeoutAsync(400);
            await page.Keyboard.PressAsync("Enter");
        }

        // navigate command -> chrome.tabs.update -> top-level load -> overlay re-inject -> capture.
        for (var i = 0; i < 20; i++)
        {
            await page.WaitForTimeoutAsync(1500);
            if (!page.Url.Contains("127.0.0.1", StringComparison.Ordinal) &&
                !page.Url.StartsWith("about:", StringComparison.Ordinal))
            {
                break;
            }
        }
    }

    try { await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(outDir, "ext-navigate.png") }); }
    catch { /* best effort */ }

    var endUrl = page.Url;
    var navigated = !endUrl.Contains("127.0.0.1", StringComparison.Ordinal)
        && !endUrl.StartsWith("about:", StringComparison.Ordinal);
    Check(navigated, $"overlay-driven navigation changed the tab top-level URL: {startUrl} -> {endUrl}");

    // workspace-blg5.7: once a live page is loaded the overlay docks to a TUI-majority split so the real
    // site shows on the right. Poll — the fresh overlay re-injects full-width, then the backend narrows it.
    var splitOk = false;
    double? splitW = null;
    var vwSplit = await page.EvaluateAsync<int>("() => window.innerWidth");
    for (var i = 0; i < 12; i++)
    {
        var ov = await page.QuerySelectorAsync("#wire-copy-overlay");
        var box = ov != null ? await ov.BoundingBoxAsync() : null;
        splitW = box?.Width;
        if (box != null && box.Width <= vwSplit * 0.8 && box.Width >= vwSplit * 0.4)
        {
            splitOk = true;
            break;
        }

        await page.WaitForTimeoutAsync(1000);
    }

    Check(splitOk, $"content loaded: overlay docked to a split (w={splitW}, vw={vwSplit}; expect ~60%)");

    // workspace-blg5.6: the navigate must actually RENDER content in the overlay — the user's complaint
    // was an EMPTY overlay. The overlay iframe was re-injected by the post-navigation reload, so re-find
    // it and poll the xterm rows for a non-trivial link list (not just a blank/skeleton terminal).
    var renderedRows = string.Empty;
    for (var i = 0; i < 15; i++)
    {
        var ovFrame = page.Frames.FirstOrDefault(f => f.Url.Contains("overlay.html", StringComparison.Ordinal));
        if (ovFrame != null)
        {
            try
            {
                renderedRows = await ovFrame.EvaluateAsync<string>(
                    "() => { const e = document.querySelector('.xterm-rows'); return e ? e.innerText : ''; }");
            }
            catch { /* frame mid-navigation */ }
        }

        // "links" / "sections" appear in the link-view header; a healthy render also has many rows.
        if (renderedRows.Contains("link", StringComparison.OrdinalIgnoreCase)
            || renderedRows.Split('\n').Count(r => r.Trim().Length > 0) >= 8)
        {
            break;
        }

        await page.WaitForTimeoutAsync(1500);
    }

    var nonBlankRows = renderedRows.Split('\n').Count(r => r.Trim().Length > 0);
    Check(nonBlankRows >= 8,
        $"overlay renders content after navigation (NOT empty) — {nonBlankRows} non-blank rows");

    // workspace-blg5.7/.9: assert the TUI actually REFLOWED to the panel — the re-injected overlay's
    // xterm must have fewer columns at the split than the full-width launcher. Catches the clipped-TUI
    // bug (iframe shrank but the terminal kept its wide column count and overran the panel).
    var splitCols = 0;
    var colsFrame = page.Frames.FirstOrDefault(f => f.Url.Contains("overlay.html", StringComparison.Ordinal));
    if (colsFrame != null)
    {
        try { splitCols = await colsFrame.EvaluateAsync<int>("() => parseInt(document.documentElement.getAttribute('data-wc-cols') || '0', 10)"); }
        catch { /* frame mid-navigation */ }
    }

    Check(splitCols > 0 && launcherCols > 0 && splitCols < launcherCols,
        $"TUI reflowed to the panel — xterm cols shrank {launcherCols} -> {splitCols} (full-width -> split)");

    // Settle for the backend's resize-driven re-render (TerminalResizeDetector polls at 100ms) before
    // the final screenshot, so we capture the reflowed layout, not the pre-re-render frame.
    await page.WaitForTimeoutAsync(7000);

    try { await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(outDir, "ext-navigate-final.png") }); }
    catch { /* best effort */ }

    // Triage from the child log regardless of pass/fail (tells us WHERE the chain broke).
    var sentNavigate = LogContains(logGlobDir, "Loading page via extension (host-browser renderer)");
    var loadFailed = LogContains(logGlobDir, "Extension page load failed");
    var loadedOk = LogContains(logGlobDir, "Loaded page via extension");
    Console.WriteLine($"   [triage] backend-sent-navigate={sentNavigate} loadFailed={loadFailed} loadedOk={loadedOk}");
}
else if (webshellMode)
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
