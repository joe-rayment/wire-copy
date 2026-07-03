// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Browser.Themes;
using WireCopy.Infrastructure.Browser.UI.StatusLine;

namespace WireCopy.Infrastructure.Browser.UI.Renderers;

/// <summary>
/// Renders a 2-line status bar at the bottom of the screen.
/// Line 1: full-width dimmed separator rule (──────)
/// Line 2: [←] MODE  leftContent     rightContent  ?:help
/// </summary>
internal class StatusBarRenderer
{
    private const string Reset = "\x1b[0m";

    private static readonly char[] SpinnerFrames = ['\u280B', '\u2819', '\u2839', '\u2838', '\u283C', '\u2834', '\u2826', '\u2827', '\u2807', '\u280F'];

    private static int _spinnerFrame;

    private readonly RenderHelpers _helpers;

    private readonly IThemeProvider _themeProvider;

    // workspace-vkhr Phase D: optional reference to the singleton background
    // job manager so the right-hand side of the status bar can render a
    // "🎧 Generating XX%" badge while the podcast modal is detached. Optional
    // because most test paths construct the renderer directly; null simply
    // means the badge never renders.
    private readonly IPodcastBackgroundJobManager? _podcastJobManager;

    public StatusBarRenderer(RenderHelpers helpers, IThemeProvider themeProvider)
        : this(helpers, themeProvider, podcastJobManager: null)
    {
    }

    public StatusBarRenderer(
        RenderHelpers helpers,
        IThemeProvider themeProvider,
        IPodcastBackgroundJobManager? podcastJobManager)
    {
        _helpers = helpers;
        _themeProvider = themeProvider;
        _podcastJobManager = podcastJobManager;
    }

    public void RenderStatusBar(
        NavigationContext context,
        ViewMode mode,
        int terminalWidth = 0,
        PreloadProgress? cacheProgress = null,
        double cacheUsagePercent = 0,
        int readerTotalLines = 0,
        int readerContentWidth = 0,
        int readerViewportHeight = 0,
        string? layoutVariantLabel = null,
        IReadOnlyList<string>? missingCookieDomains = null,
        HumanActionRequired? requiredAction = null,
        bool browserDocked = false,
        bool preloadDetailVisible = false)
    {
        _ = readerContentWidth; // workspace-wef6.2: W badge dropped - width announces transiently on [/] instead.
        var width = terminalWidth > 0 ? terminalWidth : Console.WindowWidth;
        var model = ComposeStatusLine(
            context,
            mode,
            width,
            cacheProgress,
            cacheUsagePercent,
            readerTotalLines,
            readerViewportHeight,
            layoutVariantLabel,
            missingCookieDomains,
            requiredAction,
            browserDocked,
            _podcastJobManager,
            preloadDetailVisible: preloadDetailVisible);

        RenderStatusLine(model);
    }

    /// <summary>
    /// workspace-wef6 B1: paints a fully composed <see cref="StatusLineModel"/>.
    /// All width decisions were made by <see cref="StatusComposer"/> — this is
    /// a pure theme-token painter keeping the 2-line layout (dimmed rule +
    /// content line).
    /// </summary>
    public void RenderStatusLine(StatusLineModel model)
    {
        var p = BuiltInThemes.Get(_themeProvider.CurrentTheme);
        _helpers.WriteLine(Components.Borders.DimmedRule(p, model.Width));
        _helpers.WriteLine(FormatStatusLine(model, p));
    }

    /// <summary>
    /// workspace-wef6.2: builds the channelized status items and composes them
    /// for the given width. Static and pure (modulo the spinner frame counter)
    /// so tests can assert the model without console capture.
    /// </summary>
    internal static StatusLineModel ComposeStatusLine(
        NavigationContext context,
        ViewMode mode,
        int width,
        PreloadProgress? cacheProgress = null,
        double cacheUsagePercent = 0,
        int readerTotalLines = 0,
        int readerViewportHeight = 0,
        string? layoutVariantLabel = null,
        IReadOnlyList<string>? missingCookieDomains = null,
        HumanActionRequired? requiredAction = null,
        bool browserDocked = false,
        IPodcastBackgroundJobManager? podcastJobManager = null,
        TimeProvider? clock = null,
        bool preloadDetailVisible = false)
    {
        var left = BuildLeftItems(context, mode, readerTotalLines, readerViewportHeight);
        var right = BuildRightItems(
            context,
            mode,
            cacheProgress,
            cacheUsagePercent,
            layoutVariantLabel,
            missingCookieDomains,
            requiredAction,
            browserDocked,
            podcastJobManager);
        var hints = BuildHintItem(context, mode, preloadDetailVisible);
        var help = BuildHelpItem(context);

        return new StatusComposer(clock).Compose(width, left, right, hints, help);
    }

    /// <summary>
    /// "42% \u00b7 ~6 min left" \u2014 minutes from the page's word count and the active
    /// reading speed (speed-read WPM when running, ~230 WPM prose pace
    /// otherwise). Falls back to the bare percent when no word count exists.
    /// </summary>
    internal static string FormatReaderPosition(NavigationContext context, int progressPercent)
    {
        progressPercent = Math.Clamp(progressPercent, 0, 100);
        var wordCount = context.CurrentPage?.ReadableContent?.WordCount ?? 0;
        if (wordCount <= 0 || progressPercent >= 100)
        {
            return $"{progressPercent}%";
        }

        var wpm = context.IsSpeedReadActive ? Math.Max(100, context.SpeedReadWpm) : 230;
        var minutesLeft = (int)Math.Ceiling(wordCount * ((100 - progressPercent) / 100.0) / wpm);
        return $"{progressPercent}% \u00b7 ~{minutesLeft} min left";
    }

    /// <summary>
    /// Renders the model into one ANSI line. Internal so tests can assert the
    /// painted output without console capture.
    /// </summary>
    internal static string FormatStatusLine(StatusLineModel model, ThemePalette p)
    {
        var leftParts = model.Left
            .Select(group => PaintSegments(group, p))
            .Where(s => s.Length > 0)
            .ToList();
        if (model.Hints is { Length: > 0 })
        {
            leftParts.Add(PaintSegments(model.Hints, p));
        }

        var left = string.Join(" ", leftParts);

        var separator = $" {p.SecondaryText.AnsiFg}·{Reset} ";
        var right = string.Join(separator, model.Right.Select(group => PaintSegments(group, p)).Where(s => s.Length > 0));

        var help = model.Help is { Length: > 0 } ? PaintSegments(model.Help, p) : string.Empty;
        if (help.Length > 0 && right.Length > 0)
        {
            help = $" {help}";
        }

        return $"{left}{new string(' ', model.Padding)}{right}{help}";
    }

    internal static string GetModeLabel(ViewMode mode)
    {
        // NOTE: ViewMode.Launcher is intentionally retained here even though
        // the StatusBar is not rendered for the launcher (workspace-m8x2).
        // This helper is also called by KeybindingPopup.Render to title the
        // help popup, which the launcher DOES open. The status-bar-internal
        // helpers (GetShortModeLabel, GetHintTiers) drop the launcher arm
        // because they are only reached from RenderStatusBar / GetAdaptiveHints.
        return mode switch
        {
            ViewMode.Hierarchical => "LinkView",
            ViewMode.Readable => "ReaderView",
            ViewMode.CollectionList => "Collections",
            ViewMode.CollectionItems => "ReadingList",
            ViewMode.Launcher => "Launcher",
            _ => "Browser"
        };
    }

    internal static string GetAdaptiveHints(ViewMode mode, ThemePalette p, int availableWidth)
    {
        var tiers = GetHintTiers(mode);

        foreach (var tier in tiers)
        {
            var formatted = FormatHints(p, tier);
            var displayWidth = RenderHelpers.GetDisplayWidth(formatted);
            if (displayWidth <= availableWidth)
            {
                return formatted;
            }
        }

        return string.Empty;
    }

    internal static string FormatProgressBar(int cached, int total, ThemePalette p, bool isActive = false, string? currentUrl = null)
    {
        const int barLength = 6;
        var isComplete = cached >= total && total > 0;

        if (isActive)
        {
            var spinner = SpinnerFrames[_spinnerFrame % SpinnerFrames.Length];
            _spinnerFrame++;
            var bar = Components.Indicators.RenderEighthBlockBar(
                p.GetWarningFg().AnsiFg, p.GetMutedFg().AnsiFg, (double)cached / Math.Max(1, total), barLength);
            return $"{p.SecondaryText.AnsiFg}{cached}/{total}{Reset} {bar} {p.GetWarningFg().AnsiFg}{spinner}{Reset}";
        }

        if (isComplete)
        {
            var bar = Components.Indicators.RenderEighthBlockBar(
                p.GetSuccessFg().AnsiFg, p.GetMutedFg().AnsiFg, 1.0, barLength);
            return $"{p.SecondaryText.AnsiFg}{cached}/{total}{Reset} {bar}";
        }

        return $"{p.SecondaryText.AnsiFg}{cached}/{total}{Reset}";
    }

    /// <summary>
    /// Plays a bright-green wave animation across the dimmed separator rule (line 1 of the status bar).
    /// 10 frames at 60ms = 600ms total. A segment of 4 characters sweeps left to right.
    /// Must be called from a background thread. Uses ConsoleSync.Lock for thread safety.
    /// </summary>
    internal static void PlayCacheWarmWave(ThemePalette p, int width)
    {
        if (Console.IsOutputRedirected)
        {
            return;
        }

        const int frameCount = 10;
        const int segmentWidth = 4;
        const int frameDelayMs = 60;
        var ruleWidth = Math.Max(1, width);
        var dimFg = p.GetDimFg().AnsiFg;
        var brightFg = p.PrimaryText.AnsiFg;

        for (var frame = 0; frame < frameCount; frame++)
        {
            var wavePos = (int)(frame * (ruleWidth / (double)frameCount));
            var beforeLen = Math.Max(0, wavePos);
            var brightLen = Math.Min(segmentWidth, ruleWidth - wavePos);
            var afterLen = Math.Max(0, ruleWidth - wavePos - segmentWidth);

            lock (ConsoleSync.Lock)
            {
                try
                {
                    Console.SetCursorPosition(0, Console.WindowHeight - 2);
                    Console.Write(
                        $"{dimFg}\x1b[2m{new string('\u2500', beforeLen)}{Reset}" +
                        $"{brightFg}{new string('\u2500', brightLen)}{Reset}" +
                        $"{dimFg}\x1b[2m{new string('\u2500', afterLen)}{Reset}");
                }
                catch
                {
                    // Ignore console errors (e.g., redirected output)
                }
            }

            Thread.Sleep(frameDelayMs);
        }

        // Restore the separator to its normal dimmed state
        lock (ConsoleSync.Lock)
        {
            try
            {
                Console.SetCursorPosition(0, Console.WindowHeight - 2);
                Console.Write($"{dimFg}\x1b[2m{new string('\u2500', ruleWidth)}{Reset}");
            }
            catch
            {
                // Ignore console errors
            }
        }
    }

    /// <summary>
    /// Plays a 3-frame color pulse on the cache progress count text.
    /// Cycles: AccentFg (cyan) -> PrimaryText (green) -> SecondaryText (settled).
    /// 3 frames at 100ms = 300ms total. Must be called from a background thread.
    /// </summary>
    internal static void PlayCacheItemPulse(ThemePalette p, int count, int total, int col, int row)
    {
        if (Console.IsOutputRedirected)
        {
            return;
        }

        const int frameDelayMs = 100;
        var colors = new[] { p.GetAccentFg().AnsiFg, p.PrimaryText.AnsiFg, p.SecondaryText.AnsiFg };
        var text = $"{count}/{total}";

        foreach (var color in colors)
        {
            lock (ConsoleSync.Lock)
            {
                try
                {
                    Console.SetCursorPosition(col, row);
                    Console.Write($"{color}{text}{Reset}");
                }
                catch
                {
                    // Ignore console errors
                }
            }

            Thread.Sleep(frameDelayMs);
        }
    }

    private static List<StatusItem> BuildLeftItems(
        NavigationContext context,
        ViewMode mode,
        int readerTotalLines,
        int readerViewportHeight)
    {
        var items = new List<StatusItem>();

        if (context.CanGoBack)
        {
            items.Add(StatusItem.Text(StatusChannel.Ambient, StatusStyle.Secondary, "[\u2190]"));
        }

        // workspace-wef6.2: the mode badge only renders for the collection
        // views, where the surface isn't visually self-evident. Link view and
        // reader view are obvious at a glance \u2014 mode switches announce
        // transiently instead of occupying permanent chrome.
        if (mode is ViewMode.CollectionList or ViewMode.CollectionItems)
        {
            items.Add(StatusItem.Text(StatusChannel.Ambient, StatusStyle.ModeBadge, GetModeLabel(mode)));
        }

        // Reader: "42% \u00b7 ~6 min left" replaces the L12/348 W60 42% trivia.
        if (mode == ViewMode.Readable && readerTotalLines > 0)
        {
            var progress = (int)((float)Math.Min(context.ScrollOffset + readerViewportHeight, readerTotalLines) / readerTotalLines * 100);
            items.Add(StatusItem.Text(StatusChannel.Ambient, StatusStyle.Secondary, FormatReaderPosition(context, progress)));
        }

        // Collection items: the active collection name.
        if (mode == ViewMode.CollectionItems)
        {
            var name = context.CurrentPage?.Metadata?.Title;
            if (!string.IsNullOrEmpty(name))
            {
                items.Add(StatusItem.Text(StatusChannel.Ambient, StatusStyle.Secondary, name));
            }
        }

        // workspace-wef6.2: no domain on the left in Hierarchical \u2014 the header
        // already shows it. The freed space goes to adaptive hints.
        return items;
    }

    private static List<StatusItem> BuildRightItems(
        NavigationContext context,
        ViewMode mode,
        PreloadProgress? cacheProgress,
        double cacheUsagePercent,
        string? layoutVariantLabel,
        IReadOnlyList<string>? missingCookieDomains,
        HumanActionRequired? requiredAction,
        bool browserDocked,
        IPodcastBackgroundJobManager? podcastJobManager)
    {
        var items = new List<StatusItem>();

        // ---- Alert channel: HITL verdicts; never dropped at any width ----
        if (requiredAction != null)
        {
            items.Add(BuildAlertItem(requiredAction));
        }
        else if (missingCookieDomains is { Count: > 0 })
        {
            // Legacy cookie badge \u2014 kept for consumers without the typed signal.
            var domainList = string.Join(",", missingCookieDomains);
            items.Add(new StatusItem
            {
                Channel = StatusChannel.Alert,
                Priority = 1,
                Variants = new[]
                {
                    new[]
                    {
                        new StatusSegment("\U0001F36A\u2717 ", StatusStyle.Prompt),
                        new StatusSegment(domainList, StatusStyle.Secondary),
                        new StatusSegment(" ", StatusStyle.Secondary),
                        new StatusSegment("Shift+I", StatusStyle.Accent),
                        new StatusSegment(":login", StatusStyle.Dim),
                    },
                    new[]
                    {
                        new StatusSegment("\U0001F36A\u2717", StatusStyle.Prompt),
                        new StatusSegment("\u00b7", StatusStyle.Secondary),
                        new StatusSegment("Shift+I", StatusStyle.Accent),
                    },
                },
            });
        }

        // ---- Transient channel: the active status message / announcement ----
        // TTL expiry is owned by NavigationService (clock-based), so presence
        // here means "still active" \u2014 the composer guarantees it isn't dropped.
        var transient = BuildTransientItem(context);
        if (transient != null)
        {
            items.Add(transient);
        }

        // ---- Activity channel: ONE animated "is it working" slot ----
        // Priority: foreground load > AI analysis > podcast > prefetch.
        var activity = BuildActivityItem(context, mode, cacheProgress, podcastJobManager);
        if (activity != null)
        {
            items.Add(activity);
        }

        // ---- Ambient channel: non-default states, in stable priority order ----
        if (browserDocked)
        {
            items.Add(new StatusItem
            {
                Channel = StatusChannel.Ambient,
                Priority = 0,
                Variants = new[]
                {
                    new[]
                    {
                        new StatusSegment("\u21c9", StatusStyle.Accent),
                        new StatusSegment(" docked", StatusStyle.Secondary),
                    },
                    new[] { new StatusSegment("\u21c9", StatusStyle.Accent) },
                },
            });
        }

        if (!string.IsNullOrEmpty(context.SearchQuery))
        {
            // workspace-6z3a.2: show WHERE the user is in the results —
            // "/query 2/14 (n/N)" — and be explicit when nothing matched.
            var fullVariant = context.SearchMatchCount > 0
                ? new[]
                {
                    new StatusSegment($"/{context.SearchQuery}", StatusStyle.Prompt),
                    new StatusSegment($" {context.SearchMatchIndex + 1}/{context.SearchMatchCount}", StatusStyle.Secondary),
                    new StatusSegment(" (n/N)", StatusStyle.Secondary),
                }
                : new[]
                {
                    new StatusSegment($"/{context.SearchQuery}", StatusStyle.Prompt),
                    new StatusSegment(" 0 matches", StatusStyle.Secondary),
                };

            items.Add(new StatusItem
            {
                Channel = StatusChannel.Ambient,
                Priority = 2,
                Variants = new[]
                {
                    fullVariant,
                    new[] { new StatusSegment($"/{context.SearchQuery}", StatusStyle.Prompt) },
                },
            });
        }

        if (context.IsInPreviewMode && context.PreviewLabel != null)
        {
            items.Add(StatusItem.Text(StatusChannel.Ambient, StatusStyle.Prompt, context.PreviewLabel, priority: 3));
        }
        else
        {
            // workspace-g801: the layout badge points new users at the AI setup
            // wizard ('g l'). Once the site IS configured the badge is just noise
            // shown forever, so suppress it — reconfigure lives in help / ':layout'.
            if (mode == ViewMode.Hierarchical &&
                context.CurrentPage?.Classification == PageClassification.LinkList &&
                !context.IsAiHierarchy)
            {
                items.Add(new StatusItem
                {
                    Channel = StatusChannel.Ambient,
                    Priority = 4,
                    Variants = new[]
                    {
                        new[]
                        {
                            new StatusSegment("g l", StatusStyle.Accent),
                            new StatusSegment(":set up", StatusStyle.Dim),
                        },
                    },
                });
            }

            if (!string.IsNullOrEmpty(layoutVariantLabel))
            {
                items.Add(StatusItem.Text(StatusChannel.Ambient, StatusStyle.Secondary, layoutVariantLabel, priority: 5));
            }
        }

        var cacheState = BuildCacheStateItem(context, mode, cacheProgress);
        if (cacheState != null)
        {
            items.Add(cacheState);
        }

        var selCount = context.CurrentPage?.LinkTree?.SelectionCount ?? 0;
        if (selCount > 0 && mode == ViewMode.Hierarchical)
        {
            items.Add(StatusItem.Text(StatusChannel.Ambient, StatusStyle.Prompt, $"{selCount} sel", priority: 7));
        }

        if (cacheUsagePercent >= 90)
        {
            items.Add(StatusItem.Text(StatusChannel.Ambient, StatusStyle.Prompt, $"cache {cacheUsagePercent:F0}%", priority: 8));
        }

        // While the speed-read announcement itself is on screen, the ambient
        // badge would duplicate it — it appears once the announcement fades.
        if (context.IsSpeedReadActive && context.ActiveAnnouncement?.Glyph != "▶")
        {
            items.Add(new StatusItem
            {
                Channel = StatusChannel.Ambient,
                Priority = 9,
                Variants = new[]
                {
                    new[] { new StatusSegment($"\u25b6 {context.SpeedReadWpm} WPM", StatusStyle.Prompt) },
                    new[] { new StatusSegment($"\u25b6{context.SpeedReadWpm}", StatusStyle.Prompt) },
                },
            });
        }

        return items;
    }

    /// <summary>
    /// workspace-wef6.4: the Transient item. A rich announcement renders
    /// glyph + copy + key hints ("\u25b6 Speed reading 350 WPM \u2014 &lt;:slower
    /// &gt;:faster f:stop"), degrading to glyph + copy, then to its compact
    /// short form ("\u25b6350"). Plain status messages (the SetStatusMessage shim)
    /// render as before.
    /// </summary>
    private static StatusItem? BuildTransientItem(NavigationContext context)
    {
        var announcement = context.ActiveAnnouncement;
        if (announcement == null)
        {
            return string.IsNullOrEmpty(context.StatusMessage)
                ? null
                : StatusItem.Text(StatusChannel.Transient, StatusStyle.Prompt, context.StatusMessage);
        }

        var glyphPrefix = string.IsNullOrEmpty(announcement.Glyph) ? string.Empty : $"{announcement.Glyph} ";
        var baseVariant = new List<StatusSegment>
        {
            new($"{glyphPrefix}{announcement.Text}", StatusStyle.Prompt),
        };

        var variants = new List<StatusSegment[]>();
        if (announcement.Keys.Count > 0)
        {
            var withKeys = new List<StatusSegment>(baseVariant)
            {
                new(" \u2014 ", StatusStyle.Dim),
            };
            for (var i = 0; i < announcement.Keys.Count; i++)
            {
                if (i > 0)
                {
                    withKeys.Add(new StatusSegment(" ", StatusStyle.Dim));
                }

                withKeys.Add(new StatusSegment(announcement.Keys[i].Key, StatusStyle.Accent));
                withKeys.Add(new StatusSegment($":{announcement.Keys[i].Action}", StatusStyle.Dim));
            }

            variants.Add(withKeys.ToArray());
        }

        variants.Add(baseVariant.ToArray());

        if (!string.IsNullOrEmpty(announcement.ShortText))
        {
            variants.Add(new[] { new StatusSegment(announcement.ShortText, StatusStyle.Prompt) });
        }

        return new StatusItem
        {
            Channel = StatusChannel.Transient,
            Variants = variants,
        };
    }

    /// <summary>
    /// HITL alert item: long copy names the verb + domain + recovery key;
    /// short copy keeps the glyph, verb, and key ("\u23f8 login\u00b7|").
    /// </summary>
    private static StatusItem BuildAlertItem(HumanActionRequired requiredAction)
    {
        var domainText = string.IsNullOrWhiteSpace(requiredAction.Domain) ? "site" : requiredAction.Domain;

        // workspace-kq4b: Generic verdicts lead with "uncertain interruption" +
        // Shift+R retry rather than a confidently wrong claim.
        var isGeneric = requiredAction.Variant == HumanActionVariant.Generic;
        var verb = isGeneric ? "uncertain interruption" : GetActionVerb(requiredAction.Variant);
        var key = isGeneric ? "Shift+R" : "|";
        var action = isGeneric ? "retry" : "open";

        return new StatusItem
        {
            Channel = StatusChannel.Alert,
            Variants = new[]
            {
                new[]
                {
                    new StatusSegment("\u23f8 ", StatusStyle.Warning),
                    new StatusSegment($"{verb} at {domainText}", StatusStyle.Secondary),
                    new StatusSegment(" \u00b7 ", StatusStyle.Secondary),
                    new StatusSegment(key, StatusStyle.Accent),
                    new StatusSegment($":{action}", StatusStyle.Dim),
                },
                new[]
                {
                    new StatusSegment("\u23f8 ", StatusStyle.Warning),
                    new StatusSegment(verb, StatusStyle.Secondary),
                    new StatusSegment("\u00b7", StatusStyle.Secondary),
                    new StatusSegment(key, StatusStyle.Accent),
                },
            },
        };
    }

    /// <summary>
    /// Activity item for an actively fetching preloader: count + eighth-block
    /// bar + Braille spinner frame. Null when prefetch isn't running.
    /// </summary>
    private static StatusItem? BuildCacheActivityItem(ViewMode mode, PreloadProgress? progress)
    {
        if (mode is not (ViewMode.Hierarchical or ViewMode.CollectionItems)
            || progress == null
            || progress.TotalCacheableLinks <= 0
            || progress.IsComplete
            || progress.PausedByUser
            || !progress.IsActivelyFetching)
        {
            return null;
        }

        var spinner = NextSpinnerFrame();
        var (filled, empty) = Components.Indicators.EighthBlockBarParts(
            (double)progress.CachedCount / Math.Max(1, progress.TotalCacheableLinks), 6);
        var count = $"{progress.CachedCount}/{progress.TotalCacheableLinks}";

        return new StatusItem
        {
            Channel = StatusChannel.Activity,
            Variants = new[]
            {
                new[]
                {
                    new StatusSegment($"{count} ", StatusStyle.Secondary),
                    new StatusSegment(filled, StatusStyle.Warning),
                    new StatusSegment(empty, StatusStyle.Muted),
                    new StatusSegment($" {spinner}", StatusStyle.Warning),
                },
                new[]
                {
                    new StatusSegment($"{count} ", StatusStyle.Secondary),
                    new StatusSegment(spinner.ToString(), StatusStyle.Warning),
                },
            },
        };
    }

    /// <summary>
    /// Quiet ambient cache states: paused, stalled, needs-login, paywall, and
    /// the cache-age badge. The "\u2713 cached" completion state renders nothing \u2014
    /// completion announces itself transiently and then stays silent
    /// (workspace-wef6.2).
    /// </summary>
    private static StatusItem? BuildCacheStateItem(NavigationContext context, ViewMode mode, PreloadProgress? progress)
    {
        if (mode is (ViewMode.Hierarchical or ViewMode.CollectionItems) && progress != null)
        {
            if (progress.TotalCacheableLinks > 0)
            {
                var count = $"{progress.CachedCount}/{progress.TotalCacheableLinks}";

                if (progress.IsComplete && progress.CachedCount > 0)
                {
                    // Partial completion (some links need a browser login) keeps
                    // a compact badge; full completion is silent.
                    return progress.NeedsBrowserCount > 0
                        ? StatusItem.Text(StatusChannel.Ambient, StatusStyle.Secondary, $"{count} \u2713", priority: 6)
                        : null;
                }

                if (progress.PausedByUser)
                {
                    return new StatusItem
                    {
                        Channel = StatusChannel.Ambient,
                        Priority = 6,
                        Variants = new[]
                        {
                            new[] { new StatusSegment($"\u23f8 {count} \u00b7 paused \u2014 you're using the browser", StatusStyle.Secondary) },
                            new[] { new StatusSegment($"\u23f8 {count} paused", StatusStyle.Secondary) },
                            new[] { new StatusSegment($"\u23f8 {count}", StatusStyle.Secondary) },
                        },
                    };
                }

                if (progress.IsActivelyFetching)
                {
                    return null; // The Activity channel owns the in-flight presentation.
                }

                // Stalled: not actively fetching but not complete either.
                if (progress.NeedsBrowserCount > 0)
                {
                    return new StatusItem
                    {
                        Channel = StatusChannel.Ambient,
                        Priority = 6,
                        Variants = new[]
                        {
                            new[]
                            {
                                new StatusSegment($"{count} \u00b7 ", StatusStyle.Secondary),
                                new StatusSegment("I", StatusStyle.Accent),
                                new StatusSegment(":login", StatusStyle.Secondary),
                            },
                        },
                    };
                }

                return StatusItem.Text(StatusChannel.Ambient, StatusStyle.Secondary, $"{count} \u00b7 paused", priority: 6);
            }

            if (progress.PaywalledLinkCount > 0)
            {
                if (context.CurrentPage?.HasReadableContent() == true ||
                    context.CurrentPage?.LinkTree?.TotalLinks > 0)
                {
                    return StatusItem.Text(StatusChannel.Ambient, StatusStyle.Secondary, "paywall", priority: 6);
                }

                return new StatusItem
                {
                    Channel = StatusChannel.Ambient,
                    Priority = 6,
                    Variants = new[]
                    {
                        new[]
                        {
                            new StatusSegment("paywall \u00b7 ", StatusStyle.Secondary),
                            new StatusSegment("I", StatusStyle.Accent),
                            new StatusSegment(":login", StatusStyle.Secondary),
                        },
                    },
                };
            }
        }

        if (context.IsFromCache)
        {
            return StatusItem.Text(
                StatusChannel.Ambient,
                StatusStyle.Secondary,
                RenderHelpers.FormatCacheAge(context.CachedAt),
                priority: 6);
        }

        return null;
    }

    /// <summary>
    /// workspace-wef6.5: the unified activity slot. Exactly one animated
    /// indicator renders, chosen by priority: a registered activity (foreground
    /// load 0, AI analysis 1), then a running podcast job, then prefetch
    /// derived from CacheProgress. The Braille spinner frame advances on every
    /// render so the slot visibly proves liveness.
    /// </summary>
    private static StatusItem? BuildActivityItem(
        NavigationContext context,
        ViewMode mode,
        PreloadProgress? cacheProgress,
        IPodcastBackgroundJobManager? podcastJobManager)
    {
        // Registered producers (load / AI) win over derived ones.
        var registered = context.ActiveActivity;
        var podcast = BuildPodcastActivityItem(podcastJobManager);
        if (registered != null && (podcast == null || registered.Priority <= 2))
        {
            var spinner = NextSpinnerFrame();
            var percentSuffix = registered.Percent is { } pct ? $" {pct}%" : string.Empty;
            return new StatusItem
            {
                Channel = StatusChannel.Activity,
                Variants = new[]
                {
                    new[]
                    {
                        new StatusSegment($"{spinner} ", StatusStyle.Warning),
                        new StatusSegment($"{registered.Text}{percentSuffix}", StatusStyle.Secondary),
                    },
                    new[] { new StatusSegment(spinner.ToString(), StatusStyle.Warning) },
                },
            };
        }

        return podcast ?? BuildCacheActivityItem(mode, cacheProgress);
    }

    /// <summary>
    /// Podcast generation as an activity-slot producer (replaces the bespoke
    /// ambient 🎧 badge): spinner + percent + the restore key.
    /// </summary>
    private static StatusItem? BuildPodcastActivityItem(IPodcastBackgroundJobManager? manager)
    {
        if (manager is null || !manager.HasActiveJob)
        {
            return null;
        }

        var snapshot = manager.LastSnapshot;
        var percent = snapshot is null ? 0 : Math.Clamp(snapshot.PercentComplete, 0, 100);
        var spinner = NextSpinnerFrame();

        return new StatusItem
        {
            Channel = StatusChannel.Activity,
            Variants = new[]
            {
                new[]
                {
                    new StatusSegment($"{spinner} ", StatusStyle.Warning),
                    new StatusSegment("\U0001F3A7 ", StatusStyle.Accent),
                    new StatusSegment($"Generating {percent}%", StatusStyle.Primary),
                    new StatusSegment(" · ", StatusStyle.Secondary),
                    new StatusSegment("Shift+P", StatusStyle.Accent),
                    new StatusSegment(":restore", StatusStyle.Dim),
                },
                new[]
                {
                    new StatusSegment("\U0001F3A7 ", StatusStyle.Accent),
                    new StatusSegment($"{percent}%", StatusStyle.Primary),
                },
            },
        };
    }

    private static char NextSpinnerFrame()
    {
        var frame = SpinnerFrames[_spinnerFrame % SpinnerFrames.Length];
        _spinnerFrame++;
        return frame;
    }

    /// <summary>
    /// workspace-wef6.3: the adaptive hint tiers as a Hint-channel item \u2014 the
    /// composer fills leftover space with the largest tier that fits.
    /// </summary>
    private static StatusItem? BuildHintItem(NavigationContext context, ViewMode mode, bool preloadDetailVisible = false)
    {
        if (context.IsInPreviewMode || mode == ViewMode.Launcher)
        {
            return null;
        }

        // The trailing help slot always shows "?:help" — strip the ? entry
        // from the tiers so the line never teaches the same key twice.
        var tiers = GetHintTiers(mode, context, preloadDetailVisible)
            .Select(tier => tier.Where(h => h.Key != "?").ToArray())
            .Where(tier => tier.Length > 0)
            .ToArray();
        if (tiers.Length == 0)
        {
            return null;
        }

        return new StatusItem
        {
            Channel = StatusChannel.Hint,
            Variants = tiers.Select(FormatHintSegments).ToArray(),
        };
    }

    private static StatusSegment[] FormatHintSegments((string Key, string Action)[] hints)
    {
        var segments = new List<StatusSegment>();
        for (var i = 0; i < hints.Length; i++)
        {
            if (i > 0)
            {
                segments.Add(new StatusSegment(" ", StatusStyle.Dim));
            }

            segments.Add(new StatusSegment(hints[i].Key, StatusStyle.Accent));
            segments.Add(new StatusSegment($":{hints[i].Action}", StatusStyle.Dim));
        }

        return segments.ToArray();
    }

    private static StatusItem BuildHelpItem(NavigationContext context)
    {
        if (context.IsInPreviewMode)
        {
            return new StatusItem
            {
                Channel = StatusChannel.Hint,
                Variants = new[]
                {
                    new[]
                    {
                        new StatusSegment("\u25c0/\u25b6", StatusStyle.Accent),
                        new StatusSegment(":cycle ", StatusStyle.Dim),
                        new StatusSegment("Enter", StatusStyle.Accent),
                        new StatusSegment(":save ", StatusStyle.Dim),
                        new StatusSegment("Esc", StatusStyle.Accent),
                        new StatusSegment(":cancel", StatusStyle.Dim),
                    },
                    new[]
                    {
                        new StatusSegment("\u25c0/\u25b6", StatusStyle.Accent),
                        new StatusSegment(" ", StatusStyle.Dim),
                        new StatusSegment("Enter", StatusStyle.Accent),
                        new StatusSegment(" ", StatusStyle.Dim),
                        new StatusSegment("Esc", StatusStyle.Accent),
                    },
                },
            };
        }

        return new StatusItem
        {
            Channel = StatusChannel.Hint,
            Variants = new[]
            {
                new[]
                {
                    new StatusSegment("?", StatusStyle.Accent),
                    new StatusSegment(":help", StatusStyle.Dim),
                },
                new[] { new StatusSegment("?", StatusStyle.Accent) },
            },
        };
    }

    /// <summary>
    /// workspace-wef6.3: state-sensitive hint tiers. When a modal-ish state is
    /// active, the hint slot teaches THAT state's keys instead of the generic
    /// per-view tier — the Claude Code pattern of contextual teaching.
    /// </summary>
    private static (string Key, string Action)[][] GetHintTiers(
        ViewMode mode,
        NavigationContext? context = null,
        bool preloadDetailVisible = false)
    {
        if (preloadDetailVisible)
        {
            return
            [
                [("\\", "close"), ("Esc", "close")],
                [("\\", "close")],
            ];
        }

        if (context?.IsSpeedReadActive == true && mode == ViewMode.Readable)
        {
            return
            [
                [("</>", "speed"), ("f", "stop")],
                [("f", "stop")],
            ];
        }

        if (mode == ViewMode.Hierarchical && (context?.CurrentPage?.LinkTree?.SelectionCount ?? 0) > 0)
        {
            return
            [
                [("s", "save-sel"), ("Space", "select"), ("Esc", "clear")],
                [("s", "save-sel"), ("Esc", "clear")],
                [("s", "save-sel")],
            ];
        }

        // workspace-g801: tiers are longest→shortest; the composer shows the
        // largest that fits. '?:help' is dropped here — it already renders in the
        // dedicated trailing help slot, so listing it again only wasted space and
        // crowded out real actions. Destructive keys (delete/clear) live in the
        // longest tier only, so they fall away first under squeeze.
        return mode switch
        {
            ViewMode.Hierarchical =>
            [
                [("Enter", "open"), ("s", "save"), ("|", "browser"), ("Space", "select"), ("A", "save-all"), ("Shift+R", "refresh"), ("v", "reader")],
                [("Enter", "open"), ("s", "save"), ("|", "browser"), ("v", "reader")],
                [("Enter", "open"), ("s", "save")],
            ],
            ViewMode.Readable =>
            [
                [("s", "save"), ("f", "speed-read"), ("o", "browser"), ("[]", "width"), ("Shift+R", "refresh"), ("v", "links"), ("b", "back")],
                [("s", "save"), ("f", "speed-read"), ("v", "links"), ("b", "back")],
                [("s", "save"), ("v", "links")],
            ],
            ViewMode.CollectionList =>
            [
                [("Enter", "open"), ("s", "set default"), ("d", "delete"), ("b", "back")],
                [("Enter", "open"), ("s", "set default"), ("b", "back")],
                [("Enter", "open")],
            ],
            ViewMode.CollectionItems =>
            [
                [("Enter", "open"), ("p", "podcast"), ("J/K", "reorder"), ("d", "remove"), ("Shift+X", "clear"), (":", "cmd"), ("b", "back")],
                [("Enter", "open"), ("p", "podcast"), ("d", "remove"), ("b", "back")],
                [("Enter", "open"), ("b", "back")],
                [("Enter", "open")],
            ],
            ViewMode.Launcher => throw new InvalidOperationException("StatusBar is not rendered for the launcher"),
            _ =>
            [
                [("q", "quit")],
            ],
        };
    }

    private static string FormatHints(ThemePalette p, (string Key, string Action)[] hints)
    {
        return string.Join(" ", hints.Select(h =>
            $"{p.GetAccentFg().AnsiFg}{h.Key}{Reset}{p.GetDimFg().AnsiFg}:{h.Action}{Reset}"));
    }

    private static string GetActionVerb(HumanActionVariant variant)
    {
        // Verb chosen to read naturally inside "{verb} at {domain}" so the badge
        // works as a single sentence in the status bar (workspace-0b9s).
        return variant switch
        {
            HumanActionVariant.Captcha => "captcha",
            HumanActionVariant.Login => "login",
            HumanActionVariant.CookieConsent => "consent",
            HumanActionVariant.TwoFactor => "2FA",
            HumanActionVariant.Paywall => "paywall",
            HumanActionVariant.RegionBlock => "region-block",
            HumanActionVariant.RedirectLoop => "redirect loop",
            _ => "action needed",
        };
    }

    private static string PaintSegments(StatusSegment[] segments, ThemePalette p)
        => string.Concat(segments
            .Where(s => s.Text.Length > 0)
            .Select(s => $"{StyleFor(s.Style, p)}{s.Text}{Reset}"));

    private static string StyleFor(StatusStyle style, ThemePalette p)
    {
        return style switch
        {
            StatusStyle.Primary => p.PrimaryText.AnsiFg,
            StatusStyle.Secondary => p.SecondaryText.AnsiFg,
            StatusStyle.Dim => p.GetDimFg().AnsiFg,
            StatusStyle.Accent => p.GetAccentFg().AnsiFg,
            StatusStyle.Warning => p.GetWarningFg().AnsiFg,
            StatusStyle.Prompt => p.PromptFg.AnsiFg,
            StatusStyle.Success => p.GetSuccessFg().AnsiFg,
            StatusStyle.Muted => p.GetMutedFg().AnsiFg,
            StatusStyle.ModeBadge => p.StatusBarTextFg.AnsiFg,
            _ => p.SecondaryText.AnsiFg,
        };
    }
}
