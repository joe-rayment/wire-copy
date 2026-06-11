// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using WireCopy.Domain.Entities.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Infrastructure.Browser.Cache;

namespace WireCopy.Infrastructure.Browser;

/// <summary>
/// Concert-view spotlight: while the headed browser is docked (the 'O' switcher),
/// mirrors the TUI link-tree selection onto the live page — DevTools-style
/// highlight box plus a scroll that keeps the selected story centered on screen.
///
/// <para>
/// Requests arrive synchronously from the render path on every selection change
/// and are coalesced by a latest-wins, single-flight pump: rapid j/j/j collapses
/// to one sync of the newest target, and the input loop is never blocked. Each
/// sync drives the session's dedicated LENS tab (workspace-qigc) — the spotlight
/// is the lens tab's only navigator, and fetches never touch it, so a follow-
/// navigation can neither interrupt a load nor be interrupted by one. When the
/// lens URL differs from the TUI's current page (cache hits and HTTP fetches
/// never navigate any visible page), the spotlight follow-navigates first so
/// the lens metaphor holds.
/// </para>
///
/// <para>
/// Failure semantics: a highlight that cannot be made visible on screen is a
/// failure — the page-side script removes the overlay and reports
/// <c>not-found</c>, and a throttled status hint (once per page) tells the user
/// why nothing is lit up. A stale highlight is never left behind.
/// </para>
/// </summary>
public sealed class DockSpotlight : IDisposable, IAsyncDisposable
{
    private static readonly TimeSpan FollowNavigationTimeout = TimeSpan.FromSeconds(15);

    // workspace-yx03: settle schedule for re-running the expander after a
    // follow-navigation — hydrated sites attach expander handlers well after
    // DOMContentLoaded, so the inline pass can be a silent no-op. Inter-pass
    // delays; the last pass lands ~5.5s after navigation.
    private static readonly int[] ExpandSettleDelaysMs = { 700, 800, 1500, 2500 };

    // workspace-yx03: beat between a rescue expansion and the sync retry so
    // render-on-click content (NYT "Show More in <Section>") has mounted.
    private static readonly TimeSpan ExpandMountDelay = TimeSpan.FromMilliseconds(350);

    private readonly IBrowserSession _session;
    private readonly ILogger<DockSpotlight> _logger;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly Task _pump;

    // Pending request slot: each new request overwrites the previous one.
    private SpotlightTarget? _pendingTarget;
    private bool _pendingIsClear;
    private bool _hasPending;

    // Last target applied successfully; used to dedupe unrelated re-renders
    // (toasts, progress updates) and to make clears no-ops when nothing is lit.
    private SpotlightTarget? _applied;

    // True while the pump is processing a request. A clear that arrives mid-sync
    // must be queued (not short-circuited on _applied == null) or the in-flight
    // sync could land an overlay after the clear was "handled".
    private volatile bool _busy;

    // Throttle: at most one not-found hint per page URL.
    private string? _lastHintPageUrl;

    // workspace-mctt: the last URL the SPOTLIGHT navigated the lens to. A lens
    // URL matching neither this nor the current target means the USER drove the
    // lens somewhere — never fight them; offer adoption instead.
    private string? _lastLensNav;
    private string? _divergedFromTarget;

    // workspace-yx03: background settle passes for the current follow-navigation,
    // cancelled and replaced by the next one.
    private CancellationTokenSource? _expandSettleCts;

    // workspace-yx03: the last target Sync gave up on (guarded by _gate — record
    // struct writes aren't atomic). A settle pass that expands something re-enqueues
    // it so the highlight self-heals without another keypress.
    private SpotlightTarget? _lastFailedTarget;

    // workspace-yx03: one rescue expansion per page URL (single slot, like
    // _lastHintPageUrl); re-armed by the next follow-navigation's fresh DOM.
    private string? _expandRescuePageUrl;

    private bool _disposed;

    public DockSpotlight(
        IBrowserSession session,
        ILogger<DockSpotlight> logger)
    {
        _session = session;
        _logger = logger;
        _pump = Task.Run(PumpAsync);
    }

    /// <summary>
    /// Raised (from the pump thread) when the USER navigated the lens somewhere
    /// other than what the app is reading (workspace-mctt). Carries the lens URL;
    /// the orchestrator offers adoption ('y'). Follow-navigation is suspended for
    /// that target so the user's page is never yanked away.
    /// </summary>
    public event Action<string>? LensDiverged;

    /// <summary>
    /// Sink for user-facing hints (wired to the status bar by the orchestrator).
    /// </summary>
    public Action<string>? StatusMessageSink { get; set; }

    /// <summary>
    /// Derives the spotlight target from the current view state:
    /// <list type="bullet">
    /// <item>link-tree view with a concrete link selected → highlight that anchor;</item>
    /// <item>reader view → a follow-only target that keeps the live window on the SAME
    /// article the terminal is reading, with no highlight box (workspace-nqqs);</item>
    /// <item>everything else (launcher, collections, group headers) → null = clear.</item>
    /// </list>
    /// </summary>
    public static SpotlightTarget? ResolveTarget(ViewMode viewMode, Page? page, NavigationTree? tree)
    {
        if (page == null)
        {
            return null;
        }

        // Reader view: the whole article IS the content, so there is no anchor to
        // highlight — just keep the live page on the url the terminal is reading. This is
        // the lens-coupling case main's link-tree spotlight didn't cover: cache hits and
        // headless preloads never navigate the foreground window, so without this the
        // docked page stays stale while you read (workspace-nqqs).
        if (viewMode == ViewMode.Readable)
        {
            return FollowPageTarget(page.Url);
        }

        if (viewMode != ViewMode.Hierarchical)
        {
            return null;
        }

        var selected = tree?.CurrentSelection;
        if (selected == null || selected.IsGroupHeader || string.IsNullOrEmpty(selected.Link.Url))
        {
            // No concrete story selected (fresh link list, group header) — still keep
            // the live window ON this page. Cache hits and HTTP fetches never navigate
            // the foreground window, so without this the sidecar would sit on the
            // previous page until a concrete link is picked (workspace-exbz).
            return FollowPageTarget(page.Url);
        }

        return new SpotlightTarget(page.Url, selected.Link.Url, selected.Link.DisplayText);
    }

    /// <summary>
    /// Requests that the live page highlight and scroll to the given target.
    /// Cheap synchronous enqueue; no-op when the browser isn't docked.
    /// </summary>
    public void RequestSync(SpotlightTarget target)
    {
        if (_disposed || !_session.IsDocked || !_session.HasActiveBrowser)
        {
            return;
        }

        lock (_gate)
        {
            // Unrelated re-render with an unchanged selection — nothing to do.
            if (!_hasPending && _applied == target)
            {
                return;
            }

            _pendingTarget = target;
            _pendingIsClear = false;
            _hasPending = true;
        }

        Wake();
    }

    /// <summary>
    /// Requests removal of any highlight on the live page. Deliberately NOT
    /// gated on <see cref="IBrowserSession.IsDocked"/>: the overlay must also be
    /// cleared right after undocking so a later re-dock never shows a stale box.
    /// </summary>
    public void RequestClear()
    {
        if (_disposed || !_session.HasActiveBrowser)
        {
            return;
        }

        lock (_gate)
        {
            // Nothing lit, nothing queued, nothing in flight — skip the wakeup.
            if (_applied == null && !_hasPending && !_busy)
            {
                return;
            }

            _pendingTarget = null;
            _pendingIsClear = true;
            _hasPending = true;
        }

        Wake();
    }

    // Sync disposal kept for containers that dispose synchronously (mirrors
    // BrowserSession); DI prefers DisposeAsync when both are present.
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _disposeCts.CancelAsync().ConfigureAwait(false);
        try
        {
            await _pump.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Pump is draining a sync against a possibly-dead page; don't block shutdown.
        }

        if (_expandSettleCts != null)
        {
            // Already cancelled via the linked _disposeCts — belt and braces.
            await _expandSettleCts.CancelAsync().ConfigureAwait(false);
            _expandSettleCts.Dispose();
        }

        _disposeCts.Dispose();
        _signal.Dispose();
    }

    /// <summary>
    /// Follow-only target for <paramref name="pageUrl"/>: keep the live window on the
    /// page the terminal is showing, no highlight box. Null for non-web URLs (launcher,
    /// skeleton, data:) where navigating a live window is meaningless.
    /// </summary>
    private static SpotlightTarget? FollowPageTarget(string pageUrl)
    {
        return CommandHandlers.BrowserDockCommandHandler.IsSummonableUrl(pageUrl)
            ? new SpotlightTarget(pageUrl, pageUrl, string.Empty, FollowPageOnly: true)
            : null;
    }

    private static bool UrlsMatch(string? liveUrl, string targetPageUrl)
    {
        if (string.IsNullOrEmpty(liveUrl))
        {
            return false;
        }

        return string.Equals(
            UrlNormalizer.Normalize(liveUrl),
            UrlNormalizer.Normalize(targetPageUrl),
            StringComparison.Ordinal);
    }

    private void Wake()
    {
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already signaled — the pump will pick up the latest pending slot.
        }
        catch (ObjectDisposedException)
        {
            // Disposed during shutdown.
        }
    }

    private async Task PumpAsync()
    {
        var ct = _disposeCts.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _signal.WaitAsync(ct).ConfigureAwait(false);

                while (TryTakePending(out var target, out var isClear))
                {
                    _busy = true;
                    try
                    {
                        if (isClear)
                        {
                            await ClearAsync().ConfigureAwait(false);
                        }
                        else if (target is { } t)
                        {
                            await SyncAsync(t).ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        _busy = false;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
    }

    private bool TryTakePending(out SpotlightTarget? target, out bool isClear)
    {
        lock (_gate)
        {
            target = _pendingTarget;
            isClear = _pendingIsClear;
            var had = _hasPending;
            _hasPending = false;
            _pendingTarget = null;
            _pendingIsClear = false;
            return had;
        }
    }

    private async Task SyncAsync(SpotlightTarget target)
    {
        if (!_session.IsDocked || !_session.HasActiveBrowser)
        {
            _applied = null;
            return;
        }

        try
        {
            var page = await _session.GetLensPageAsync().ConfigureAwait(false);
            if (page == null)
            {
                _applied = null;
                return;
            }

            // Superseded while waiting for the page — the newer request will run next.
            if (HasNewerPending())
            {
                return;
            }

            if (!UrlsMatch(page.Url, target.PageUrl))
            {
                // workspace-mctt: a lens URL we did NOT navigate to means the user
                // drove the lens themselves — don't yank their page away. Offer
                // adoption once; keep honoring their choice while the app stays on
                // the same page. The moment the app moves on, the NEW page wins
                // and following resumes.
                var userDroveLens = _lastLensNav != null
                    && !UrlsMatch(page.Url, _lastLensNav)
                    && CommandHandlers.BrowserDockCommandHandler.IsSummonableUrl(page.Url);
                if (userDroveLens)
                {
                    if (_divergedFromTarget == null)
                    {
                        _divergedFromTarget = target.PageUrl;
                        LensDiverged?.Invoke(page.Url);
                        return;
                    }

                    if (UrlsMatch(target.PageUrl, _divergedFromTarget))
                    {
                        return;
                    }
                }

                _divergedFromTarget = null;
                _logger.LogDebug(
                    "Dock spotlight: live page {LiveUrl} != TUI page {PageUrl}, follow-navigating",
                    page.Url,
                    target.PageUrl);
                await page.GotoAsync(target.PageUrl, new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = (float)FollowNavigationTimeout.TotalMilliseconds,
                }).ConfigureAwait(false);
                _lastLensNav = target.PageUrl;
                _expandRescuePageUrl = null; // fresh DOM — re-arm the once-per-page rescue

                // workspace-2cz4: mobile-viewport sites tuck content behind
                // "read more" — proactively expand on OUR navigations so listed
                // stories have layout when the spotlight looks for them.
                try
                {
                    var expanded = await page.EvaluateAsync<int>(ExpandScript.ExpandAll).ConfigureAwait(false);
                    if (expanded > 0)
                    {
                        _logger.LogDebug("Dock spotlight: auto-expanded {Count} collapsed region(s) on {Url}", expanded, target.PageUrl);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Dock spotlight: auto-expand failed (advisory)");
                }

                // workspace-yx03: hydrated sites attach the expanders' handlers
                // AFTER DOMContentLoaded, so the pass above can be a no-op —
                // keep re-running it in the background until a pass changes
                // nothing.
                StartExpandSettlePasses(page, target.PageUrl);
            }
            else
            {
                _divergedFromTarget = null;
                _lastLensNav ??= target.PageUrl;
            }

            // Reader view (workspace-nqqs): the follow-navigation above IS the whole job —
            // the page is the content, so there is no anchor to light up. Drop any box left
            // over from a prior link selection and treat the navigation alone as success.
            if (target.FollowPageOnly)
            {
                await page.EvaluateAsync<string>(SpotlightScript.Clear).ConfigureAwait(false);
                _applied = target;
                return;
            }

            var result = await page.EvaluateAsync<string>(
                SpotlightScript.Sync,
                new { url = target.LinkUrl, text = target.DisplayText }).ConfigureAwait(false);

            // workspace-2cz4: a present-but-hidden anchor usually means a
            // collapsed "read more" region — expand the region the target lives
            // in (never following a real link) and retry the spotlight once.
            if (result != "ok")
            {
                var revealed = await page.EvaluateAsync<string>(
                    ExpandScript.RevealTarget,
                    new { url = target.LinkUrl, text = target.DisplayText }).ConfigureAwait(false);
                if (revealed == "expanded")
                {
                    _logger.LogDebug("Dock spotlight: expanded a collapsed region for {LinkUrl}, retrying", target.LinkUrl);
                    result = await page.EvaluateAsync<string>(
                        SpotlightScript.Sync,
                        new { url = target.LinkUrl, text = target.DisplayText }).ConfigureAwait(false);
                }
                else if (revealed == "not-found"
                    && !string.Equals(_expandRescuePageUrl, target.PageUrl, StringComparison.Ordinal))
                {
                    // workspace-yx03: an anchor absent from the DOM is the
                    // render-on-click signature (NYT "Show More in <Section>"
                    // mounts the stories only when clicked) — the proactive pass
                    // may have clicked before hydration. One more expander pass
                    // per page, a beat for the content to mount, then retry.
                    _expandRescuePageUrl = target.PageUrl;
                    var acted = 0;
                    try
                    {
                        acted = await page.EvaluateAsync<int>(ExpandScript.ExpandAll).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Dock spotlight: rescue expand failed (advisory)");
                    }

                    if (acted > 0)
                    {
                        _logger.LogDebug("Dock spotlight: rescue pass expanded {Count} region(s) for {LinkUrl}, retrying", acted, target.LinkUrl);
                        await Task.Delay(ExpandMountDelay).ConfigureAwait(false);
                        result = await page.EvaluateAsync<string>(
                            SpotlightScript.Sync,
                            new { url = target.LinkUrl, text = target.DisplayText }).ConfigureAwait(false);
                    }
                }
            }

            if (result == "ok")
            {
                _applied = target;
                lock (_gate)
                {
                    _lastFailedTarget = null;
                }

                return;
            }

            _applied = null;
            lock (_gate)
            {
                // workspace-yx03: remember the miss so a settle pass that expands
                // something can re-enqueue it — the highlight self-heals once the
                // page finishes mounting its collapsed sections.
                _lastFailedTarget = target.FollowPageOnly ? null : target;
            }

            HintOncePerPage(target.PageUrl, "Selected story isn't visible on the live page — highlight skipped");
        }
        catch (OperationCanceledException)
        {
            // Shutdown or lease-wait timeout; the next selection move retries.
        }
        catch (PlaywrightException ex) when (PageLoader.LooksLikeStalePlaywrightPage(ex.Message))
        {
            // Page navigated/closed out from under us — drop; next move retries.
            _logger.LogDebug(ex, "Dock spotlight: stale page during sync, dropping");
            _applied = null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Dock spotlight: sync failed for {LinkUrl}", target.LinkUrl);
            _applied = null;
            HintOncePerPage(target.PageUrl, "Couldn't sync the live page to your selection");
        }
    }

    // workspace-yx03: kicks off the post-navigation settle passes, replacing any
    // loop still running for the previous navigation.
    private void StartExpandSettlePasses(IPage page, string pageUrl)
    {
        _expandSettleCts?.Cancel();
        _expandSettleCts?.Dispose();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token);
        _expandSettleCts = cts;
        _ = Task.Run(() => ExpandSettlePassesAsync(page, pageUrl, cts.Token), CancellationToken.None);
    }

    // workspace-yx03: re-runs the expander on a short settle schedule after a
    // follow-navigation. Hydrated sites attach expander click handlers after
    // DOMContentLoaded, so the inline pass can be a silent no-op; ExpandAll's
    // eligibility checks make re-runs idempotent (a worked expander disappears,
    // flips aria-expanded, or changes label). Stops as soon as a pass performs
    // no work, and never touches a lens that has moved off the page (user
    // navigation or a newer follow-nav). When a pass expands something and the
    // last sync had failed, the target is re-enqueued so the highlight
    // self-heals without another keypress.
    private async Task ExpandSettlePassesAsync(IPage page, string pageUrl, CancellationToken ct)
    {
        try
        {
            foreach (var delayMs in ExpandSettleDelaysMs)
            {
                await Task.Delay(delayMs, ct).ConfigureAwait(false);
                if (!UrlsMatch(page.Url, pageUrl))
                {
                    return;
                }

                var acted = await page.EvaluateAsync<int>(ExpandScript.ExpandAll).ConfigureAwait(false);
                if (acted == 0)
                {
                    return;
                }

                _logger.LogDebug("Dock spotlight: settle pass expanded {Count} region(s) on {Url}", acted, pageUrl);

                SpotlightTarget? failed;
                lock (_gate)
                {
                    failed = _lastFailedTarget;
                }

                if (failed is { } f && UrlsMatch(pageUrl, f.PageUrl))
                {
                    RequestSync(f);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer navigation or shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Dock spotlight: expand settle pass failed (advisory)");
        }
    }

    private async Task ClearAsync()
    {
        if (_applied == null || !_session.HasActiveBrowser)
        {
            _applied = null;
            return;
        }

        try
        {
            var page = await _session.GetLensPageAsync().ConfigureAwait(false);
            if (page != null)
            {
                await page.EvaluateAsync<string>(SpotlightScript.Clear).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
        catch (Exception ex)
        {
            // A failed clear is cosmetic at worst (window is hidden or page gone).
            _logger.LogDebug(ex, "Dock spotlight: clear failed (non-fatal)");
        }
        finally
        {
            _applied = null;
            lock (_gate)
            {
                _lastFailedTarget = null; // selection gone — nothing to self-heal
            }
        }
    }

    private bool HasNewerPending()
    {
        lock (_gate)
        {
            return _hasPending;
        }
    }

    private void HintOncePerPage(string pageUrl, string message)
    {
        if (string.Equals(_lastHintPageUrl, pageUrl, StringComparison.Ordinal))
        {
            return;
        }

        _lastHintPageUrl = pageUrl;
        StatusMessageSink?.Invoke(message);
    }
}
