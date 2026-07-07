// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Infrastructure.Configuration;

public class BrowserConfiguration
{
    public const string SectionName = "Browser";

    /// <summary>
    /// The fetch/prefetch pages' emulated viewport (workspace-v7g7). The browser context runs
    /// with NO Playwright-managed viewport (so the OS window is ours to park/place — a
    /// context-level viewport makes Patchright size the window to fit it and revert our moves);
    /// these constants re-apply per page the exact 1280x720 the app has always rendered and
    /// extracted at, so scraping, AI-layout screenshots, and site behavior are identical in
    /// every park mode and window size.
    /// </summary>
    public const int FetchViewportWidth = 1280;

    /// <inheritdoc cref="FetchViewportWidth"/>
    public const int FetchViewportHeight = 720;

    public string BrowserType { get; init; } = "Chrome"; // "Chrome" or "Firefox" - Primary browser

    public string FallbackBrowserType { get; init; } = "Firefox"; // Fallback browser if primary is blocked

    /// <summary>
    /// SUPERSEDED by <see cref="Visibility"/> (workspace-8cf2) and no longer read by
    /// the app — kept only so existing settings files bind without error.
    /// </summary>
    public bool Headless { get; init; } = true;

    /// <summary>
    /// The browser visibility policy (workspace-8cf2). Kept only so existing settings files bind
    /// without error — it no longer influences anything: the browser is ALWAYS visible (headful).
    /// An explicit Visibility=Headless is IGNORED (surfaced in <see cref="DescribeVisibilityResolution"/>).
    /// </summary>
    public BrowserVisibility Visibility { get; init; } = BrowserVisibility.Auto;

    // NEVER-HEADLESS LAW (workspace-8ne3, plumbing deleted in workspace-9k27.10): the browser is
    // NEVER headless — headless is bot-detected and blocked on the sites this app targets
    // (Cloudflare, NYT, macleans.ca). There is deliberately NO EffectiveHeadless property (and no
    // headless knob anywhere): the single Playwright launch chokepoint,
    // BrowserSession.LaunchBrowserAsync, hardcodes `Headless = false`. On a display-less host the
    // app runs under a virtual display (the `run` script provides Xvfb) — it fails loudly rather
    // than degrade to headless. Do NOT reintroduce a resolved-headless flag here; any code that
    // needs "is the browser headless?" already knows the answer: it isn't.

    /// <summary>
    /// When true, disables all UI animations. The timer-tick mechanism in the input handler
    /// will not fire, and animation frames will be skipped. Useful for low-resource environments
    /// or terminals that don't handle rapid redraws well.
    /// </summary>
    public bool DisableAnimations { get; init; }

    /// <summary>
    /// Which side of the screen the headed browser docks to when toggled into the
    /// side-by-side "concert" view. Right (default) keeps the terminal on the left.
    /// </summary>
    public DockSide DockSide { get; init; } = DockSide.Right;

    /// <summary>
    /// Fraction of the screen width the docked browser window occupies. Used only
    /// as a CLAMP/fallback when <see cref="DockWidthPx"/> is unset or larger than
    /// the screen allows. Clamped to [0.2, 0.8] at use.
    /// </summary>
    public double DockFraction { get; init; } = 0.5;

    /// <summary>
    /// Preferred docked window width in physical pixels (workspace-o5yf). A phone-
    /// shaped sidecar: responsive sites collapse to a single column, the followed
    /// article is always on-screen, and most of the screen stays with the terminal.
    /// 0 disables and falls back to <see cref="DockFraction"/>. Default 390 — an
    /// iPhone-SE-width window (workspace-75ng): the 375 px <see cref="LensViewportWidth"/>
    /// content plus the window frame/scrollbar.
    /// </summary>
    public int DockWidthPx { get; init; } = 390;

    /// <summary>
    /// CSS viewport width applied to the LENS tab (workspace-o5yf) so pages render
    /// mobile-width regardless of exact window chrome. 0 leaves the viewport fluid.
    /// Default 375 — the iPhone SE / iPhone 8 logical width (workspace-75ng).
    /// </summary>
    public int LensViewportWidth { get; init; } = 375;

    /// <summary>
    /// Whether the sidecar — the docked live browser window beside the terminal —
    /// engages automatically when a page opens (workspace-exbz). When true, completing
    /// a navigation (live OR cache-served) summons the headed window to the configured
    /// <see cref="DockSide"/>/<see cref="DockFraction"/> and the dock spotlight keeps
    /// it following the selection; the dock key switches to the immersive (full-width)
    /// view and back. When false, the sidecar only appears on an explicit dock keystroke.
    ///
    /// <para>
    /// Defaults to FALSE (workspace-75ng): the headed browser launches PARKED off-screen
    /// (immersive) so it renders fully for extraction but never pops up or steals keyboard
    /// focus at startup — the macOS focus-steal the user hit on Ghostty. 'O' brings it
    /// on-screen to dock; dismissing re-parks it off-screen. Set true to opt back into
    /// auto-docking the live window beside the terminal on every navigation.
    /// </para>
    /// </summary>
    public bool Sidecar { get; init; }

    /// <summary>
    /// Side-by-side tiling (workspace-75ng.4, macOS only). When true, docking the sidecar via
    /// 'O' also RESIZES the terminal window to the slice not taken by the browser (terminal
    /// left, iPhone-SE browser right, heights matched), and dismissing restores the terminal
    /// to its pre-dock bounds. Requires ACCESSIBILITY permission for the terminal app (System
    /// Settings → Privacy &amp; Security → Accessibility) — separate from the Automation grant
    /// used for focus return. Degrades gracefully to the plain right-edge dock if the terminal
    /// bundle id was not captured or the resize is not permitted. Default OFF: it pokes a
    /// third-party window and the reflow is verifiable only on a real Mac.
    /// </summary>
    public bool TileTerminalWithSidecar { get; init; }

    /// <summary>
    /// workspace-9k27.17: X/Y the headed window is PARKED at when hidden. Far
    /// negative so it clears any real multi-display arrangement while staying
    /// windowState=normal (Chromium keeps painting it — instant re-dock).
    /// Tunable because window managers differ: some Linux WMs clamp/refuse the
    /// move, and Windows treats -32000 as the legacy minimize coordinate.
    /// </summary>
    public int ParkCoordinate { get; init; } = -32000;

    /// <summary>
    /// How the headed window is hidden when parked (workspace-v7g7). <see cref="ParkMode.Auto"/>
    /// resolves to <see cref="ParkMode.Corner"/> on macOS — which clamps off-screen coordinates
    /// back on-screen, leaving the old off-screen park as a stray sliver — and
    /// <see cref="ParkMode.Offscreen"/> everywhere else. Override explicitly to force a mode
    /// (the Linux e2e gates run <see cref="ParkMode.Corner"/> under a real WM this way).
    /// </summary>
    public ParkMode ParkMode { get; init; } = ParkMode.Auto;

    /// <summary>
    /// The parked corner tile's outer size (workspace-v7g7, <see cref="ParkMode.Corner"/> only).
    /// Pages keep rendering at the fixed <see cref="FetchViewportWidth"/>x<see cref="FetchViewportHeight"/>
    /// emulated viewport regardless of this window size (the tile shows the page's top-left crop),
    /// so resizing the tile never changes scraping behavior. If the user drags or resizes the
    /// tile by hand, their placement is adopted; the corner is only re-asserted after flows that
    /// summon the window (dock, captcha/login).
    /// </summary>
    public int CornerParkWidth { get; init; } = 800;

    /// <inheritdoc cref="CornerParkWidth"/>
    public int CornerParkHeight { get; init; } = 600;

    /// <summary>Gap between the corner tile and the work-area edges (workspace-v7g7).</summary>
    public int CornerParkMargin { get; init; } = 8;

    /// <summary>The park mode with <see cref="ParkMode.Auto"/> resolved for this platform.</summary>
    public ParkMode EffectiveParkMode
    {
        get
        {
            if (ParkMode != ParkMode.Auto)
            {
                return ParkMode;
            }

            return OperatingSystem.IsMacOS() ? ParkMode.Corner : ParkMode.Offscreen;
        }
    }

    /// <summary>
    /// workspace-9k27.17: delay before the forced terminal refocus after a dock
    /// (lets the browser's BringToFront settle first so the refocus wins).
    /// Raise on a slow machine if focus lands on the browser after docking.
    /// </summary>
    public int DockRefocusDelayMs { get; init; } = 180;

    /// <summary>
    /// workspace-9k27.17: pause between work-area stability reads while the
    /// just-anchored window re-associates with its display.
    /// </summary>
    public int DockSettleDelayMs { get; init; } = 60;

    /// <summary>
    /// Browser-side input within this window means the user is ACTIVELY using the
    /// shared browser — prefetch pauses immediately (workspace-mya7).
    /// </summary>
    public int TakeoverInputWindowSeconds { get; init; } = 10;

    /// <summary>
    /// The browser must be quiet for this long before a user-paused prefetch
    /// resumes from its checkpoint (workspace-mya7).
    /// </summary>
    public int TakeoverResumeIdleSeconds { get; init; } = 25;

    /// <summary>
    /// Gets implicit wait timeout in seconds. Pages with heavy JavaScript and dynamic content
    /// require longer waits. Recommended: 30+ seconds for reliable element detection.
    /// </summary>
    public int ImplicitWaitSeconds { get; init; } = 10;

    /// <summary>
    /// Gets page load timeout in seconds. Some pages can be very slow to fully load, especially with
    /// images disabled and extensive JavaScript processing. A generous timeout
    /// prevents false timeouts while waiting for document.readyState to complete.
    /// </summary>
    public int PageLoadTimeoutSeconds { get; init; } = 30;

    public bool DisableImages { get; init; } = true;

    public bool DisableJavaScript { get; init; } = false;

    public string UserAgent { get; init; } = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    /// <summary>
    /// Gets optional delay (ms) after document.readyState is complete.
    /// Use for sites that need extra time for JS rendering. Default: 0 (no delay).
    /// </summary>
    public int PostLoadDelayMs { get; init; } = 500;

    /// <summary>
    /// Gets HTTP fetch timeout (ms) before falling back to browser.
    /// A short timeout ensures fast fallback to browser for JS-heavy sites.
    /// </summary>
    public int HttpTimeoutMs { get; init; } = 3000;

    /// <summary>
    /// Gets the interval (ms) between bot challenge page polls.
    /// After detecting a bot challenge, the browser re-checks the page source at this interval.
    /// </summary>
    public int BotChallengePollIntervalMs { get; init; } = 3000;

    /// <summary>
    /// Gets the maximum wait time (ms) for a bot challenge to auto-resolve.
    /// If the challenge persists beyond this duration, the page load is reported as a failure.
    /// </summary>
    public int BotChallengeMaxWaitMs { get; init; } = 60000;

    /// <summary>
    /// Domains known to require authenticated browser sessions (paywall).
    /// When cookies exist for these domains, skip HTTP and use the browser directly.
    /// </summary>
    public string[] PaywalledDomains { get; init; } = ["nytimes.com", "wsj.com", "washingtonpost.com", "theathletic.com"];

    public string[] ExperimentalOptions { get; init; } =
    [
        "excludeSwitches", "enable-automation",
        "useAutomationExtension", "false"
    ];

    /// <summary>
    /// Checks whether the given URL belongs to a known paywalled domain.
    /// Matches both exact domain (e.g., "nytimes.com") and subdomains (e.g., "www.nytimes.com").
    /// </summary>
    public bool IsPaywalledDomain(string url)
    {
        if (PaywalledDomains.Length == 0)
        {
            return false;
        }

        try
        {
            var host = new Uri(url).Host;
            return PaywalledDomains.Any(d =>
                host.Equals(d, StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith("." + d, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>One-line explanation of the resolution, logged at startup (workspace-8cf2).</summary>
    public string DescribeVisibilityResolution()
    {
        // Headless is disabled by hard project policy — the browser is ALWAYS headful. We still log the
        // interactive/display context (useful when a headed launch fails for lack of a display) and flag a
        // requested-but-ignored Visibility=Headless.
        var ignored = Visibility == BrowserVisibility.Headless
            ? " — Visibility=Headless was REQUESTED but is IGNORED (never-headless policy)"
            : string.Empty;
        return $"Browser mode: VISIBLE (headful only — never headless; interactive={IsInteractiveSession()}, "
            + $"display={HasDisplay()}){ignored}";
    }

    private static bool IsInteractiveSession()
    {
        try
        {
            return !Console.IsInputRedirected && !Console.IsOutputRedirected;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool HasDisplay()
    {
        if (!OperatingSystem.IsLinux())
        {
            return true; // macOS / Windows always have a display server
        }

        return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
    }
}
