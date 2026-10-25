// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Application.DTOs.Browser;
using WireCopy.Infrastructure.Browser.Themes;
using WireCopy.Infrastructure.Browser.UI.Renderers;
using WireCopy.Infrastructure.BrowserExtension;

namespace WireCopy.Infrastructure.Browser.CommandHandlers;

/// <summary>
/// Settings "Browser extension" card (workspace-m7aml.3, workspace-rzz8o.2): a 1Password-style install guide.
/// Lists the browsers found on this computer, each in one of three states the user can read at a glance —
/// not added yet, waiting (the guide page is open in that browser and the card polls for its hello), or
/// added. Enter opens the local step-by-step guide page in the selected browser; 'f' shows the extension
/// folder. The status text and row summary are pure functions so they are unit-testable.
/// </summary>
internal static class BrowserExtensionCard
{
    private const string Reset = "\x1b[0m";
    private const string GenericBrowserKey = "browser";
    private const int TickMilliseconds = 1000;
    private static readonly string[] VendorPrefixes = ["Google ", "Microsoft "];
    private static readonly string[] SpinnerFrames = ["◐", "◓", "◑", "◒"];

    private static IReadOnlyList<BrowserRegistration>? _cachedRegistrations;
    private static int _warmUpStarted;

    /// <summary>What the selected browser's row — and so the card's hint and footer — shows.</summary>
    private enum CardState
    {
        NoBrowser,
        NotAdded,
        Waiting,
        OpenFailed,
        Connected,
    }

    /// <summary>The last browser detection result, or null before any registration ran.</summary>
    internal static IReadOnlyList<BrowserRegistration>? CachedRegistrations => _cachedRegistrations;

    /// <summary>
    /// Fills <see cref="CachedRegistrations"/> once in the background so the Settings row can resolve the
    /// generic "browser" connection. Only the real desktop app does this (same rule as the startup service:
    /// registration points browsers at THIS binary, so a test host or unattended verb must not do it).
    /// </summary>
    internal static void WarmUp()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(Shell.ShellChannel.EnvVar))
            || _cachedRegistrations is not null
            || Interlocked.Exchange(ref _warmUpStarted, 1) == 1)
        {
            return;
        }

        _ = StartRegister();
    }

    /// <summary>Short browser name for the Settings row: "Google Chrome" becomes "Chrome".</summary>
    internal static string ShortName(string displayName)
    {
        var prefix = VendorPrefixes.FirstOrDefault(p => displayName.StartsWith(p, StringComparison.Ordinal));
        return prefix is null ? displayName : displayName[prefix.Length..];
    }

    /// <summary>Browser name for a connections.json id when no detection result names it.</summary>
    internal static string NameForId(string id) => id switch
    {
        "chrome" => "Chrome",
        "chromium" => "Chromium",
        "vivaldi" => "Vivaldi",
        "brave" => "Brave",
        "edge" => "Edge",
        "arc" => "Arc",
        _ => id,
    };

    /// <summary>
    /// Maps each detected browser's id to its connection. A generic "browser" entry (the launcher could not be
    /// identified) counts for the first detected browser, but only when no detected browser has an entry of its own.
    /// </summary>
    internal static IReadOnlyDictionary<string, BrowserConnection> ResolveConnections(
        IReadOnlyList<BrowserRegistration> registrations,
        IReadOnlyDictionary<string, BrowserConnection> connections)
    {
        var resolved = new Dictionary<string, BrowserConnection>();
        BrowserRegistration? firstDetected = null;
        foreach (var registration in registrations.Where(r => r.Detected))
        {
            firstDetected ??= registration;
            if (connections.TryGetValue(registration.Id, out var own))
            {
                resolved[registration.Id] = own;
            }
        }

        if (resolved.Count == 0 && firstDetected is not null
            && connections.TryGetValue(GenericBrowserKey, out var generic))
        {
            resolved[firstDetected.Id] = generic;
        }

        return resolved;
    }

    /// <summary>
    /// workspace-58l5s: true once any browser extension has said hello — the same notion the Settings
    /// row uses (<see cref="RowSummary"/> != "Not set up"). Drives the launcher's install hint.
    /// </summary>
    internal static bool AnyConnected() =>
        RowSummary(CachedRegistrations ?? [], BrowserConnections.Read(BrowserExtensionPaths.ConnectionsPath())) != "Not set up";

    /// <summary>
    /// The Settings row value: "Not set up", or "Connected · Chrome" (several: "Connected · Chrome, Vivaldi",
    /// most recently seen first).
    /// </summary>
    internal static string RowSummary(
        IReadOnlyList<BrowserRegistration> registrations,
        IReadOnlyDictionary<string, BrowserConnection> connections)
    {
        var seen = new List<(string Name, DateTime LastSeenUtc)>();
        var resolved = ResolveConnections(registrations, connections);
        foreach (var (id, connection) in resolved)
        {
            var registration = registrations.First(r => r.Id == id);
            seen.Add((ShortName(registration.DisplayName), connection.LastSeenUtc));
        }

        // Browsers that said hello but are not detected right now (or before detection ran) still count.
        foreach (var (id, connection) in connections)
        {
            if (id != GenericBrowserKey && !resolved.ContainsKey(id))
            {
                seen.Add((NameForId(id), connection.LastSeenUtc));
            }
        }

        if (seen.Count == 0 && connections.ContainsKey(GenericBrowserKey))
        {
            seen.Add(("your browser", connections[GenericBrowserKey].LastSeenUtc));
        }

        if (seen.Count == 0)
        {
            return "Not set up";
        }

        var names = seen.OrderByDescending(s => s.LastSeenUtc).Select(s => s.Name).Distinct();
        return "Connected · " + string.Join(", ", names);
    }

    /// <summary>The per-browser status: "✓ added · last used 2m ago" or "not added yet".</summary>
    internal static string StatusText(BrowserConnection? connection, DateTime nowUtc) =>
        connection is null
            ? "not added yet"
            : "✓ added · last used " + SettingsCommandHandler.FormatRelativeTime(nowUtc - connection.LastSeenUtc.ToUniversalTime());

    /// <summary>The row status while the guide is open and the browser has not said hello yet: "◐ waiting for Chrome…".</summary>
    internal static string WaitingText(string shortName, int spinnerFrame) =>
        $"{SpinnerFrames[Math.Abs(spinnerFrame) % SpinnerFrames.Length]} waiting for {shortName}…";

    /// <summary>
    /// Everything that changes what the card shows; the render loop only redraws when this changes. The spinner
    /// frame is part of the key only for a waiting browser, so an idle card does not redraw every tick.
    /// </summary>
    internal static string BuildStateKey(
        IReadOnlyList<BrowserRegistration> registrations,
        IReadOnlyDictionary<string, BrowserConnection> connections,
        DateTime nowUtc,
        int selectedIndex,
        string? message,
        int width,
        int height,
        IReadOnlyCollection<string>? waiting = null,
        IReadOnlyCollection<string>? openFailed = null,
        int spinnerFrame = 0)
    {
        var resolved = ResolveConnections(registrations, connections);
        var rows = registrations.Where(r => r.Detected)
            .Select(r => r.Id + "=" + RowState(r, resolved, waiting, openFailed) switch
            {
                CardState.Waiting => WaitingText(ShortName(r.DisplayName), spinnerFrame),
                CardState.OpenFailed => "open failed",
                _ => StatusText(resolved.GetValueOrDefault(r.Id), nowUtc),
            });
        return string.Join(";", rows) + $"|{selectedIndex}|{message}|{width}x{height}";
    }

    /// <summary>Runs the card until Esc; the caller re-renders the Settings screen afterwards.</summary>
    internal static async Task RunAsync(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        var registrations = _cachedRegistrations ?? Array.Empty<BrowserRegistration>();
        var registerTask = StartRegister();
        var selected = 0;
        var userPicked = false;
        string? message = null;
        var ticks = 0;

        // Per browser id, for this visit to the card: the guide page was opened and the browser has not said
        // hello yet / the browser could not be opened at all. Both clear the moment the browser connects.
        var waiting = new HashSet<string>(StringComparer.Ordinal);
        var openFailed = new HashSet<string>(StringComparer.Ordinal);
        var lastState = string.Empty;
        using var keyCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task<NavigationCommand>? pendingKeyTask = null;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (registerTask is { IsCompleted: true })
                {
                    registrations = (registerTask.IsCompletedSuccessfully ? registerTask.Result : null) ?? registrations;
                    registerTask = null;
                }

                var detected = registrations.Where(r => r.Detected).ToList();
                selected = Selection(detected, selected, userPicked);
                var connections = BrowserConnections.Read(BrowserExtensionPaths.ConnectionsPath());
                var resolved = ResolveConnections(registrations, connections);
                waiting.RemoveWhere(resolved.ContainsKey);
                openFailed.RemoveWhere(resolved.ContainsKey);
                var now = DateTime.UtcNow;
                var spinnerFrame = ticks % SpinnerFrames.Length;
                var state = BuildStateKey(
                    registrations,
                    connections,
                    now,
                    selected,
                    message,
                    options.TerminalWidth,
                    options.TerminalHeight,
                    waiting,
                    openFailed,
                    spinnerFrame);
                if (state != lastState)
                {
                    Render(ctx, options, detected, resolved, now, selected, message, waiting, openFailed, spinnerFrame);
                    lastState = state;
                }

                pendingKeyTask ??= ctx.InputHandler.WaitForInputAsync(keyCts.Token);
                using var tickCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var tickTask = Task.Delay(TickMilliseconds, tickCts.Token);
                Task completed = registerTask is null
                    ? await Task.WhenAny(pendingKeyTask, tickTask).ConfigureAwait(false)
                    : await Task.WhenAny(pendingKeyTask, tickTask, registerTask).ConfigureAwait(false);
                await tickCts.CancelAsync().ConfigureAwait(false);

                if (completed == tickTask)
                {
                    ticks++;
                }

                if (completed != pendingKeyTask)
                {
                    continue;
                }

                var command = await pendingKeyTask.ConfigureAwait(false);
                pendingKeyTask = null;

                if (command.Type == CommandType.TerminalResized)
                {
                    options = ctx.GetCurrentRenderOptions();
                }
                else if (command.Type is CommandType.GoBack or CommandType.Quit)
                {
                    break;
                }
                else if (command.Type == CommandType.MoveDown && detected.Count > 0)
                {
                    selected = (selected + 1) % detected.Count;
                    userPicked = true;
                    message = null;
                }
                else if (command.Type == CommandType.MoveUp && detected.Count > 0)
                {
                    selected = (selected - 1 + detected.Count) % detected.Count;
                    userPicked = true;
                    message = null;
                }
                else if (command.Type is CommandType.ActivateLink or CommandType.ToggleSpeedRead)
                {
                    // Make sure the folder and host manifest exist before pointing the user (or the browser) at them.
                    registerTask ??= StartRegister();
                    registrations = await registerTask.ConfigureAwait(false) ?? registrations;
                    registerTask = null;
                    detected = registrations.Where(r => r.Detected).ToList();
                    selected = Selection(detected, selected, userPicked);

                    // Enter adds to the selected browser (opens the guide page in it); 'f' (ToggleSpeedRead in the
                    // key map) shows the extension folder. Without a browser, Enter shows the folder as well.
                    if (command.Type == CommandType.ToggleSpeedRead || detected.Count == 0)
                    {
                        message = await RevealFolderAsync().ConfigureAwait(false);
                        continue;
                    }

                    var browser = detected[selected];
                    message = null;
                    if (await OpenGuideAsync(browser).ConfigureAwait(false))
                    {
                        openFailed.Remove(browser.Id);
                        waiting.Add(browser.Id);
                    }
                    else
                    {
                        waiting.Remove(browser.Id);
                        openFailed.Add(browser.Id);
                    }
                }
            }
        }
        finally
        {
            // Cancel and await the orphaned key read so it cannot steal the Settings screen's next key.
            await PodcastSetupHelpers.DrainPendingKeyAsync(pendingKeyTask, keyCts).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The row the card starts on: Chrome when it is detected, else the first browser found; once the user has
    /// moved the selection it is only clamped to the list.
    /// </summary>
    private static int Selection(List<BrowserRegistration> detected, int current, bool userPicked)
    {
        if (detected.Count == 0)
        {
            return 0;
        }

        if (userPicked)
        {
            return Math.Min(current, detected.Count - 1);
        }

        var chrome = detected.FindIndex(r => r.Id == "chrome");
        return chrome < 0 ? 0 : chrome;
    }

    private static CardState RowState(
        BrowserRegistration browser,
        IReadOnlyDictionary<string, BrowserConnection> resolved,
        IReadOnlyCollection<string>? waiting,
        IReadOnlyCollection<string>? openFailed)
    {
        if (resolved.ContainsKey(browser.Id))
        {
            return CardState.Connected;
        }

        if (waiting?.Contains(browser.Id) == true)
        {
            return CardState.Waiting;
        }

        return openFailed?.Contains(browser.Id) == true ? CardState.OpenFailed : CardState.NotAdded;
    }

    private static Task<IReadOnlyList<BrowserRegistration>?> StartRegister() => Task.Run(
        () =>
        {
            try
            {
                var result = NativeHostRegistrar.Register(
                    NativeHostEnvironment.FromCurrentProcess(),
                    NativeHostCommand.FromCurrentProcess());
                _cachedRegistrations = result;
                return (IReadOnlyList<BrowserRegistration>?)result;
            }
            catch (Exception)
            {
                return null;
            }
        });

    /// <summary>Opens the local step-by-step guide page in the browser; true when the browser took it.</summary>
    private static Task<bool> OpenGuideAsync(BrowserRegistration browser) => Task.Run(() =>
    {
        var guide = BrowserExtensionPaths.GuidePath();
        return File.Exists(guide)
            && BrowserExtensionLauncher.OpenGuidePage(
                browser, ShortName(browser.DisplayName), guide, BrowserExtensionPaths.ExtensionDir());
    });

    private static Task<string> RevealFolderAsync() => Task.Run(() =>
    {
        var folder = BrowserExtensionPaths.ExtensionDir();
        return Directory.Exists(folder) && BrowserExtensionLauncher.RevealFolder(folder)
            ? "Opened the extension folder."
            : "Couldn't open the extension folder: " + folder;
    });

    private static void Render(
        CommandContext ctx,
        RenderOptions options,
        List<BrowserRegistration> detected,
        IReadOnlyDictionary<string, BrowserConnection> resolved,
        DateTime now,
        int selected,
        string? message,
        IReadOnlyCollection<string> waiting,
        IReadOnlyCollection<string> openFailed,
        int spinnerFrame)
    {
        var palette = BuiltInThemes.Get(ctx.ThemeProvider.CurrentTheme);
        var helpers = new RenderHelpers { TerminalHeight = options.TerminalHeight };
        helpers.Clear();
        var width = Math.Max(20, options.TerminalWidth - 2);

        var browser = detected.Count == 0 ? null : detected[selected];
        var name = browser is null ? "your browser" : ShortName(browser.DisplayName);
        var state = browser is null ? CardState.NoBrowser : RowState(browser, resolved, waiting, openFailed);

        helpers.WriteLine();
        helpers.WriteLine($"{palette.HeaderBorderFg.AnsiFg}╭{new string('─', width - 2)}╮{Reset}");
        var title = RenderHelpers.TruncateText($"Save articles from {name}", width - 4);
        helpers.WriteLine(
            $"{palette.HeaderBorderFg.AnsiFg}│ {palette.HeaderTitleFg.AnsiFg}" +
            $"{title.PadRight(width - 4)}{palette.HeaderBorderFg.AnsiFg} │{Reset}");
        helpers.WriteLine($"{palette.HeaderBorderFg.AnsiFg}╰{new string('─', width - 2)}╯{Reset}");
        helpers.WriteLine();

        WriteWrapped(
            helpers,
            width,
            palette.SecondaryText.AnsiFg,
            $"Add the Wire Copy button to {name} once. Then one click saves the article you're reading " +
            "to your Reading List — even offline.");

        helpers.WriteLine();
        if (detected.Count > 1)
        {
            helpers.WriteLine($"  {palette.SecondaryText.AnsiFg}Browsers on this computer{Reset}");
        }

        if (detected.Count == 0)
        {
            helpers.WriteLine(
                $"    {palette.GetWarningFg().AnsiFg}No Chrome, Vivaldi, Brave, Edge or Chromium found on this computer.{Reset}");
        }

        for (var i = 0; i < detected.Count; i++)
        {
            helpers.WriteLine(
                BuildRow(palette, width, detected[i], i == selected, resolved, waiting, openFailed, now, spinnerFrame));
        }

        helpers.WriteLine();
        switch (state)
        {
            case CardState.NotAdded:
                WriteWrapped(helpers, width, palette.PrimaryText.AnsiFg, $"Takes about a minute — {name} walks you through it.");
                break;
            case CardState.Waiting:
                WriteWrapped(
                    helpers,
                    width,
                    palette.PrimaryText.AnsiFg,
                    $"Follow the steps in {name}. This screen updates by itself when Wire Copy arrives.");
                break;
            case CardState.OpenFailed:
                WriteWrapped(
                    helpers,
                    width,
                    palette.GetWarningFg().AnsiFg,
                    $"Couldn't open {name}. Open chrome://extensions in it yourself, then press f to show the folder.");
                break;
            case CardState.Connected:
                WriteWrapped(
                    helpers,
                    width,
                    palette.GetSuccessFg().AnsiFg,
                    $"✓ Wire Copy is in {name}. Click the W button on any article to save it here.");
                break;
        }

        if (message is not null)
        {
            helpers.WriteLine();
            helpers.WriteLine($"  {palette.GetAccentFg().AnsiFg}{RenderHelpers.TruncateText(message, width - 4)}{Reset}");
        }

        helpers.WriteLine();
        var accent = palette.GetAccentFg().AnsiFg;
        var dim = palette.GetDimFg().AnsiFg;
        var keys = new List<string>();
        switch (state)
        {
            case CardState.NoBrowser:
                keys.Add($"{accent}Enter{Reset}{dim} open extension folder{Reset}");
                break;
            case CardState.Waiting:
                keys.Add($"{accent}Enter{Reset}{dim} show the steps again{Reset}");
                keys.Add($"{accent}f{Reset}{dim} show the folder{Reset}");
                break;
            case CardState.Connected:
                keys.Add($"{accent}Enter{Reset}{dim} set up again{Reset}");
                break;
            case CardState.OpenFailed:
                keys.Add($"{accent}Enter{Reset}{dim} add to {name}{Reset}");
                keys.Add($"{accent}f{Reset}{dim} show the folder{Reset}");
                break;
            default:
                keys.Add($"{accent}Enter{Reset}{dim} add to {name}{Reset}");
                break;
        }

        if (detected.Count > 1)
        {
            keys.Add($"{accent}↑↓{Reset}{dim} browser{Reset}");
        }

        keys.Add($"{accent}Esc{Reset}{dim} back{Reset}");
        helpers.WriteLine("  " + string.Join("   ", keys));
        helpers.ClearRemainingLines();
    }

    private static string BuildRow(
        ThemePalette palette,
        int width,
        BrowserRegistration browser,
        bool isSelected,
        IReadOnlyDictionary<string, BrowserConnection> resolved,
        IReadOnlyCollection<string> waiting,
        IReadOnlyCollection<string> openFailed,
        DateTime now,
        int spinnerFrame)
    {
        switch (RowState(browser, resolved, waiting, openFailed))
        {
            case CardState.Connected:
                return SettingsRowRenderer.Build(
                    palette,
                    width,
                    isSelected,
                    isWarning: false,
                    "●",
                    palette.GetSuccessFg().AnsiFg,
                    browser.DisplayName,
                    StatusText(resolved[browser.Id], now),
                    palette.GetSuccessFg().AnsiFg,
                    "Set up again").MainLine;
            case CardState.Waiting:
                return BuildActionlessRow(
                    palette, width, isSelected, browser.DisplayName, WaitingText(ShortName(browser.DisplayName), spinnerFrame));
            default:
                return SettingsRowRenderer.Build(
                    palette,
                    width,
                    isSelected,
                    isWarning: false,
                    "○",
                    palette.GetDimFg().AnsiFg,
                    browser.DisplayName,
                    StatusText(null, now),
                    palette.SecondaryText.AnsiFg,
                    "Add").MainLine;
        }
    }

    /// <summary>
    /// A row laid out like <see cref="SettingsRowRenderer.Build"/> but with no [Enter] action — the waiting row
    /// has nothing for the user to do on it.
    /// </summary>
    private static string BuildActionlessRow(ThemePalette palette, int width, bool isSelected, string label, string value)
    {
        const int labelCol = 24;
        const int overhead = 2 /*margin*/ + 1 /*gutter*/ + 1 /*gap*/ + 2 /*icon + space*/;
        var paddedLabel = label.Length < labelCol ? label.PadRight(labelCol) : label;
        var budget = Math.Max(0, width - overhead - paddedLabel.Length);
        var shown = value.Length <= budget ? value : SettingsRowRenderer.TruncateValueMiddle(value, budget);
        var indicator = $"{palette.GetMutedFg().AnsiFg}{(isSelected ? "▌" : " ")}{Reset}";
        var content =
            $"{palette.GetDimFg().AnsiFg}○{Reset} {palette.PrimaryText.AnsiFg}{paddedLabel}{Reset}" +
            $"{palette.GetAccentFg().AnsiFg}{shown}{Reset}";
        if (!isSelected)
        {
            return $"  {indicator} {content}";
        }

        var pad = Math.Max(0, width - overhead - paddedLabel.Length - shown.Length);
        return $"  {indicator} {palette.SelectedItemBg.AnsiBg}{palette.SelectedItemFg.AnsiFg}{content}{new string(' ', pad)}{Reset}";
    }

    private static void WriteWrapped(RenderHelpers helpers, int width, string colour, string text)
    {
        foreach (var line in GcsCopy.WrapToWidth(text, width - 4))
        {
            helpers.WriteLine($"  {colour}{line}{Reset}");
        }
    }
}
