// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Application.DTOs.Browser;
using WireCopy.Infrastructure.Browser.Themes;
using WireCopy.Infrastructure.Browser.UI.Renderers;
using WireCopy.Infrastructure.BrowserExtension;

namespace WireCopy.Infrastructure.Browser.CommandHandlers;

/// <summary>
/// Settings "Browser extension" card (workspace-m7aml.3): a 1Password-style install guide. Lists the
/// browsers found on this computer with a live "connected" status, tells the user the two steps to add
/// the extension, and on Enter opens a local step-by-step guide page in the browser plus the extension folder. The
/// status text and row summary are pure functions so they are unit-testable.
/// </summary>
internal static class BrowserExtensionCard
{
    private const string Reset = "\x1b[0m";
    private const string GenericBrowserKey = "browser";
    private const int TickMilliseconds = 1000;
    private static readonly string[] VendorPrefixes = ["Google ", "Microsoft "];

    private static IReadOnlyList<BrowserRegistration>? _cachedRegistrations;
    private static int _warmUpStarted;

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

    /// <summary>The per-browser status: "✓ connected · last used 2m ago" or "ready to add".</summary>
    internal static string StatusText(BrowserConnection? connection, DateTime nowUtc) =>
        connection is null
            ? "ready to add"
            : "✓ connected · last used " + SettingsCommandHandler.FormatRelativeTime(nowUtc - connection.LastSeenUtc.ToUniversalTime());

    /// <summary>Everything that changes what the card shows; the render loop only redraws when this changes.</summary>
    internal static string BuildStateKey(
        IReadOnlyList<BrowserRegistration> registrations,
        IReadOnlyDictionary<string, BrowserConnection> connections,
        DateTime nowUtc,
        int selectedIndex,
        string? message,
        int width,
        int height)
    {
        var resolved = ResolveConnections(registrations, connections);
        var rows = registrations.Where(r => r.Detected)
            .Select(r => r.Id + "=" + StatusText(resolved.GetValueOrDefault(r.Id), nowUtc));
        return string.Join(";", rows) + $"|{selectedIndex}|{message}|{width}x{height}";
    }

    /// <summary>Runs the card until Esc; the caller re-renders the Settings screen afterwards.</summary>
    internal static async Task RunAsync(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        var registrations = _cachedRegistrations ?? Array.Empty<BrowserRegistration>();
        var registerTask = StartRegister();
        var selected = 0;
        string? message = null;
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
                selected = detected.Count == 0 ? 0 : Math.Min(selected, detected.Count - 1);
                var connections = BrowserConnections.Read(BrowserExtensionPaths.ConnectionsPath());
                var now = DateTime.UtcNow;
                var state = BuildStateKey(
                    registrations, connections, now, selected, message, options.TerminalWidth, options.TerminalHeight);
                if (state != lastState)
                {
                    Render(ctx, options, detected, ResolveConnections(registrations, connections), now, selected, message);
                    lastState = state;
                }

                pendingKeyTask ??= ctx.InputHandler.WaitForInputAsync(keyCts.Token);
                using var tickCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var tickTask = Task.Delay(TickMilliseconds, tickCts.Token);
                Task completed = registerTask is null
                    ? await Task.WhenAny(pendingKeyTask, tickTask).ConfigureAwait(false)
                    : await Task.WhenAny(pendingKeyTask, tickTask, registerTask).ConfigureAwait(false);
                await tickCts.CancelAsync().ConfigureAwait(false);

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
                    message = null;
                }
                else if (command.Type == CommandType.MoveUp && detected.Count > 0)
                {
                    selected = (selected - 1 + detected.Count) % detected.Count;
                    message = null;
                }
                else if (command.Type == CommandType.ActivateLink)
                {
                    // Make sure the folder and host manifest exist before pointing the user at them.
                    registerTask ??= StartRegister();
                    registrations = await registerTask.ConfigureAwait(false) ?? registrations;
                    registerTask = null;
                    detected = registrations.Where(r => r.Detected).ToList();
                    selected = detected.Count == 0 ? 0 : Math.Min(selected, detected.Count - 1);
                    message = await OpenAsync(detected.Count == 0 ? null : detected[selected]).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            // Cancel and await the orphaned key read so it cannot steal the Settings screen's next key.
            await PodcastSetupHelpers.DrainPendingKeyAsync(pendingKeyTask, keyCts).ConfigureAwait(false);
        }
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

    private static Task<string> OpenAsync(BrowserRegistration? browser) => Task.Run(() =>
    {
        var folder = BrowserExtensionPaths.ExtensionDir();
        var folderOpened = Directory.Exists(folder) && BrowserExtensionLauncher.RevealFolder(folder);
        if (browser is null)
        {
            return folderOpened
                ? "Opened the extension folder."
                : "Couldn't open the extension folder: " + folder;
        }

        var shortName = ShortName(browser.DisplayName);
        var guide = BrowserExtensionPaths.GuidePath();
        var pageOpened = File.Exists(guide) && BrowserExtensionLauncher.OpenGuidePage(browser, shortName, guide, folder);
        return pageOpened && folderOpened
            ? $"Opened {shortName} with the steps, and the extension folder."
            : $"Couldn't open it — open chrome://extensions in {shortName} yourself";
    });

    private static void Render(
        CommandContext ctx,
        RenderOptions options,
        List<BrowserRegistration> detected,
        IReadOnlyDictionary<string, BrowserConnection> resolved,
        DateTime now,
        int selected,
        string? message)
    {
        var palette = BuiltInThemes.Get(ctx.ThemeProvider.CurrentTheme);
        var helpers = new RenderHelpers { TerminalHeight = options.TerminalHeight };
        helpers.Clear();
        var width = Math.Max(20, options.TerminalWidth - 2);

        helpers.WriteLine();
        helpers.WriteLine($"{palette.HeaderBorderFg.AnsiFg}╭{new string('─', width - 2)}╮{Reset}");
        var title = RenderHelpers.TruncateText("Save articles from your browser", width - 4);
        helpers.WriteLine(
            $"{palette.HeaderBorderFg.AnsiFg}│ {palette.HeaderTitleFg.AnsiFg}" +
            $"{title.PadRight(width - 4)}{palette.HeaderBorderFg.AnsiFg} │{Reset}");
        helpers.WriteLine($"{palette.HeaderBorderFg.AnsiFg}╰{new string('─', width - 2)}╯{Reset}");
        helpers.WriteLine();

        foreach (var line in GcsCopy.WrapToWidth(
            "The Wire Copy button saves the article you're reading to your Reading List, " +
            "straight from the page your browser already has — even offline.",
            width - 4))
        {
            helpers.WriteLine($"  {palette.SecondaryText.AnsiFg}{line}{Reset}");
        }

        helpers.WriteLine();
        helpers.WriteLine($"  {palette.SecondaryText.AnsiFg}Browsers on this computer{Reset}");

        if (detected.Count == 0)
        {
            helpers.WriteLine(
                $"    {palette.GetWarningFg().AnsiFg}No Chrome, Vivaldi, Brave, Edge or Chromium found on this computer.{Reset}");
        }

        var rowIds = detected.Select(d => d.Id).ToArray();
        for (var i = 0; i < detected.Count; i++)
        {
            var connection = resolved.GetValueOrDefault(detected[i].Id);
            var connected = connection is not null;
            var (mainLine, _) = SettingsRowRenderer.Build(
                palette,
                width,
                isSelected: i == selected,
                isWarning: false,
                connected ? "●" : "○",
                connected ? palette.GetSuccessFg().AnsiFg : palette.GetDimFg().AnsiFg,
                detected[i].DisplayName,
                StatusText(connection, now),
                connected ? palette.GetSuccessFg().AnsiFg : palette.GetDimFg().AnsiFg,
                connected ? "Manage" : "Add");
            helpers.WriteLine(mainLine);
        }

        helpers.WriteLine();
        var selectedBrowser = detected.Count == 0 ? null : detected[selected];
        var folder = BrowserExtensionPaths.ExtensionDir();
        if (selectedBrowser is not null && resolved.ContainsKey(selectedBrowser.Id))
        {
            helpers.WriteLine(
                $"  {palette.GetSuccessFg().AnsiFg}✓ Wire Copy is in {ShortName(selectedBrowser.DisplayName)}. " +
                $"Click its toolbar button on any article to save it.{Reset}");
        }
        else
        {
            var target = selectedBrowser is null ? "your browser" : ShortName(selectedBrowser.DisplayName);
            helpers.WriteLine($"  {palette.PrimaryText.AnsiFg}Add it to {target} — 2 steps{Reset}");
            WriteStep(helpers, palette, width, "1. Open chrome://extensions and turn on Developer mode (top-right)");
            WriteStep(
                helpers,
                palette,
                width,
                "2. Drag the WireCopy-Extension folder onto that page — or click Load unpacked and choose it");
            foreach (var line in GcsCopy.WrapToWidth(folder, width - 10))
            {
                helpers.WriteLine($"        {palette.GetAccentFg().AnsiFg}{line}{Reset}");
            }
        }

        if (message is not null)
        {
            helpers.WriteLine();
            helpers.WriteLine($"  {palette.GetAccentFg().AnsiFg}{RenderHelpers.TruncateText(message, width - 4)}{Reset}");
        }

        helpers.WriteLine();
        var nav = rowIds.Length > 1 ? $"{palette.GetAccentFg().AnsiFg}↑↓{Reset}{palette.GetDimFg().AnsiFg} browser   " : string.Empty;
        var enter = detected.Count == 0 ? "open extension folder" : "open steps in browser + folder";
        helpers.WriteLine(
            $"  {nav}{palette.GetAccentFg().AnsiFg}Enter{Reset}{palette.GetDimFg().AnsiFg} {enter}   " +
            $"{palette.GetAccentFg().AnsiFg}Esc{Reset}{palette.GetDimFg().AnsiFg} back{Reset}");
        helpers.ClearRemainingLines();
    }

    private static void WriteStep(RenderHelpers helpers, ThemePalette palette, int width, string text)
    {
        var lines = GcsCopy.WrapToWidth(text, width - 10);
        for (var i = 0; i < lines.Count; i++)
        {
            helpers.WriteLine($"    {palette.SecondaryText.AnsiFg}{(i == 0 ? string.Empty : "   ")}{lines[i]}{Reset}");
        }
    }
}
