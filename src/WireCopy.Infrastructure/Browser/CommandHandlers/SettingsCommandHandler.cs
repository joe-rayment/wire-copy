// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WireCopy.Application.DTOs;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.DTOs.Podcast;
using WireCopy.Application.Interfaces;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Application.Interfaces.Podcast;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Infrastructure.Browser.Themes;
using WireCopy.Infrastructure.Browser.UI.Components;
using WireCopy.Infrastructure.Browser.UI.Renderers;
using WireCopy.Infrastructure.Configuration;
using WireCopy.Infrastructure.Podcast;
using WireCopy.Infrastructure.Podcast.AudioEngine;
using WireCopy.Infrastructure.Scheduling;

namespace WireCopy.Infrastructure.Browser.CommandHandlers;

/// <summary>
/// Handles settings, :set, and :clear commands for API keys and configuration.
///
/// The unified Setup screen (workspace-fn1u) extends <see cref="HandleConfigScreen"/>
/// with row-based selection covering every credential and every preference:
/// OpenAI API key (AI Curated layout), the audio-engine connection, podcast
/// output folder, and the auto-purge window.
///
/// Both this screen and <see cref="PodcastSetupHelpers"/> share the row
/// renderer (<see cref="SettingsRowRenderer"/>) and the same Enter handlers so
/// values set in either place persist via <see cref="IUserSettingsStore"/>.
/// </summary>
internal static class SettingsCommandHandler
{
    /// <summary>
    /// Setting keys persisted via <see cref="IUserSettingsStore"/>. Sharing the
    /// names as constants keeps the row dispatch and the first-run check in lock
    /// step.
    /// </summary>
    internal const string KeyOpenAiApiKey = "OpenAiApiKey";
    internal const string KeyPodcastOutputFolder = "PodcastOutputFolder";
    internal const string KeyOutputRetentionHours = "PodcastOutputRetentionHours";
    internal const string KeyPodcastCostGateAlwaysShow = "PodcastCostGateAlwaysShow";

    // workspace-c3ec4.3: audio-engine connection (must match AudioEngineSettings.Resolve).
    internal const string KeyAudioEngineUrl = "AudioEngineUrl";
    internal const string KeyAudioEngineApiKey = "AudioEngineApiKey";
    internal const string KeyAudioEngineProgram = "AudioEngineProgram";  // workspace-08e4u: folder or executable WireCopy starts for the user

    // workspace-r8on: layout-judge model + optional local (Ollama) endpoint.
    internal const string KeyLayoutModel = "LayoutModel";
    internal const string KeyLayoutEndpoint = "LayoutEndpoint";
    internal const string KeyLayoutApiKey = "LayoutApiKey";
    internal const string DefaultOllamaEndpoint = "http://localhost:11434/v1";

    private const string Reset = "\x1b[0m";

    /// <summary>Upper bound on one audio-engine status probe from the Setup screen.</summary>
    private static readonly TimeSpan NarratorProbeTimeout = TimeSpan.FromSeconds(2);

    /// <summary>How often the idle Settings screen re-probes audio-engine (workspace-c3ec4.6).</summary>
    private static readonly TimeSpan NarratorStatusRefresh = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Selectable rows on the unified Setup screen. Each row dispatches Enter to
    /// the matching prompt/picker and persists via <see cref="IUserSettingsStore"/>.
    /// </summary>
    internal enum SetupRow
    {
        OpenAiKey,
        OutputFolder,
        AutoPurgeHours,
        CostGateAlwaysShow,
        LayoutModel,

        // workspace-c3ec4.3: audio-engine connection rows.
        AudioEngine,
        AudioEngineProgram,  // workspace-08e4u: folder or executable WireCopy starts when nothing answers
        AudioEngineKey,

        // workspace-fqc5b / workspace-wfykm: read-only rows showing what audio-engine will do; Enter opens it.
        Narration,
        Publishing,

        // workspace-m7aml.3: install guide for the browser extension.
        BrowserExtension,
    }

    /// <summary>
    /// First-run detection: returns true when the OpenAI key (the only
    /// credential Wire Copy itself holds) has not been configured.
    /// </summary>
    internal static bool IsFirstRun(IUserSettingsStore settingsStore)
    {
        ArgumentNullException.ThrowIfNull(settingsStore);

        return string.IsNullOrWhiteSpace(settingsStore.Get(KeyOpenAiApiKey));
    }

    /// <summary>
    /// Returns true when the primary credential is still
    /// unconfigured. Drives the launcher's "press c" Setup hint inside the
    /// header card (workspace-9qzh) — partial-setup users still need a path
    /// into Setup. Distinct from <see cref="IsFirstRun"/>, which is the
    /// stricter "no credential at all" predicate used for the welcome banner.
    /// </summary>
    internal static bool HasIncompleteSetup(IUserSettingsStore settingsStore)
    {
        ArgumentNullException.ThrowIfNull(settingsStore);

        return IsFirstRun(settingsStore);
    }

    /// <summary>
    /// Unified <c>:config</c> Setup screen. Lists every credential and every
    /// preference as a selectable row; Enter edits via the existing FormField /
    /// picker handlers. Esc returns to the launcher.
    ///
    /// When <paramref name="showWelcomeBanner"/> is true a one-line "Welcome —
    /// set up Wire Copy" banner is shown above the rows (first-run only).
    /// </summary>
    internal static async Task HandleConfigScreen(
        CommandContext ctx,
        RenderOptions options,
        CancellationToken ct,
        bool showWelcomeBanner = false)
    {
        var selectedIndex = 0;
        BrowserExtensionCard.WarmUp();

        // workspace-c3ec4.3: audio-engine status is probed once up front and again only
        // after a row that can change it — render reads the cached value, never the network.
        NarratorStatus? narratorStatus = null;
        var narratorStatusFresh = false;
        NarrationFeed? defaultFeed = null;
        string? paintedFeedText = null;

        // workspace-08e4u: the status words last painted on the Audio engine row, so the 3 s
        // refresh repaints "starting…" → "ready ✓" / "not running" even when the probe looks the same.
        string? paintedNarratorText = null;

        // workspace-c3ec4.6: the input wait outlives a status-driven repaint so a keystroke
        // that lands mid-repaint is still consumed by the next iteration, never dropped.
        Task<NavigationCommand>? pendingInput = null;

        // workspace-rzz8o.1: first body line on screen. Lives outside the loop so the view
        // stays put across the 3 s status repaints and only moves when the selection leaves it.
        var bodyOffset = 0;

        while (!ct.IsCancellationRequested)
        {
            using var scope = ctx.ScopeFactory.CreateScope();
            var settingsStore = scope.ServiceProvider.GetRequiredService<IUserSettingsStore>();
            var launcher = scope.ServiceProvider.GetRequiredService<IAudioEngineLauncher>();
            if (!narratorStatusFresh)
            {
                narratorStatus = await ProbeNarratorAsync(scope, ct).ConfigureAwait(false);
                defaultFeed = narratorStatus?.Reachable == true ? await ProbeDefaultFeedAsync(scope, ct).ConfigureAwait(false) : null;
                narratorStatusFresh = true;

                // workspace-08e4u: nothing answers at a local URL — start audio-engine for the user
                // (one automatic attempt per URL per session; the row shows "starting…" meanwhile).
                if (narratorStatus?.ErrorKind == NarrationErrorKind.Unreachable && AudioEngineLauncher.IsLocal(narratorStatus.Url))
                {
                    _ = launcher.BeginStart(narratorStatus.Url);
                }
            }

            var rows = BuildSetupRows();
            selectedIndex = Math.Min(selectedIndex, rows.Length - 1);
            var palette = BuiltInThemes.Get(ctx.ThemeProvider.CurrentTheme);
            var helpers = new RenderHelpers { TerminalHeight = options.TerminalHeight };

            // This screen is painted straight into the terminal, never through a
            // Render* method, so the shell's HTML launcher / link-tree pane would
            // keep covering it. Hide the panes (a no-op once hidden or in terminal
            // mode); the render on exit shows the right pane again.
            ctx.Renderer.LeaveHtmlPanes();
            helpers.Clear();

            var width = Math.Max(20, options.TerminalWidth - 2);

            // Header box
            helpers.WriteLine();
            helpers.WriteLine($"{palette.HeaderBorderFg.AnsiFg}╭{new string('─', width - 2)}╮{Reset}");
            var title = RenderHelpers.TruncateText("Settings", width - 4);
            helpers.WriteLine(
                $"{palette.HeaderBorderFg.AnsiFg}│ {palette.HeaderTitleFg.AnsiFg}" +
                $"{title.PadRight(width - 4)}{palette.HeaderBorderFg.AnsiFg} │{Reset}");
            helpers.WriteLine($"{palette.HeaderBorderFg.AnsiFg}╰{new string('─', width - 2)}╯{Reset}");
            helpers.WriteLine();

            if (showWelcomeBanner)
            {
                helpers.WriteLine(
                    $"  {palette.GetAccentFg().AnsiFg}Welcome — set up Wire Copy{Reset}");
                helpers.WriteLine(
                    $"  {palette.SecondaryText.AnsiFg}Configure as much or as little as you like. Esc skips for now.{Reset}");
                helpers.WriteLine();
            }

            // Resolve current values
            var hasOpenAi = !string.IsNullOrWhiteSpace(settingsStore.Get(KeyOpenAiApiKey));
            var outputFolder = settingsStore.Get(KeyPodcastOutputFolder)
                               ?? ResolveDefaultOutputFolder();
            var purgeHours = ResolvePurgeHours(scope, settingsStore);
            var costGateAlwaysShow = bool.TryParse(
                settingsStore.Get(KeyPodcastCostGateAlwaysShow), out var alwaysShowParsed)
                && alwaysShowParsed;

            // workspace-r8on: layout-judge model — OpenAI (default) or a local Ollama.
            var layoutEndpoint = settingsStore.Get(KeyLayoutEndpoint);
            var layoutIsLocal = !string.IsNullOrWhiteSpace(layoutEndpoint);
            var layoutModelName = settingsStore.Get(KeyLayoutModel) ?? "gpt-5-nano";
            var layoutDisplay = layoutIsLocal ? $"Local · {layoutModelName}" : $"OpenAI · {layoutModelName}";

            var audioEngineUrl = settingsStore.Get(KeyAudioEngineUrl) ?? AudioEngineUrlFromEnv() ?? ResolveTtsDefault(scope, c => c.Url, "http://127.0.0.1:8080");

            // workspace-c3ec4.6: the key row mirrors AudioEngineSettings.Resolve — a saved key
            // first, else the AUDIO_ENGINE_API_KEY environment variable — so an env-supplied
            // key never reads "not set" while narration is in fact authorised.
            var storedAudioEngineKey = !string.IsNullOrWhiteSpace(settingsStore.Get(KeyAudioEngineApiKey));
            var envAudioEngineKey = !storedAudioEngineKey
                && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AudioEngineSettings.ApiKeyEnvVar));
            var hasAudioEngineKey = storedAudioEngineKey || envAudioEngineKey;
            var audioEngineKeyValue = "not set";
            if (storedAudioEngineKey)
            {
                audioEngineKeyValue = "configured";
            }
            else if (envAudioEngineKey)
            {
                audioEngineKeyValue = $"from {AudioEngineSettings.ApiKeyEnvVar}";
            }

            var audioEngineReady = narratorStatus?.Ready == true;
            paintedNarratorText = DescribeNarratorStatus(narratorStatus, narratorStatus is not null && launcher.IsStarting(narratorStatus.Url));
            var audioEngineValue = $"{audioEngineUrl} · {paintedNarratorText}";

            // workspace-08e4u: the program WireCopy starts when nothing answers at the URL above.
            var programDiscovery = launcher.Discover();
            var programFound = programDiscovery.Program is not null;
            var programValue = programFound ? programDiscovery.Program!.Display : "not found";

            // workspace-rzz8o.1: the sections below are captured, not painted — there are more
            // rows than a short terminal has lines, so a window of them is painted after the
            // last row, scrolled to keep the selected row on screen.
            helpers.BeginCapture();

            // ---- Credentials (single OpenAI key powers both TTS and AI Curated) ----
            helpers.WriteLine($"  {palette.SecondaryText.AnsiFg}Credentials{Reset}");
            RenderRow(
                helpers,
                palette,
                width,
                rows,
                selectedIndex,
                SetupRow.OpenAiKey,
                hasOpenAi ? "●" : "○",
                hasOpenAi ? palette.PromptFg.AnsiFg : palette.SecondaryText.AnsiFg,
                "OpenAI API key",
                hasOpenAi ? "configured" : "not set",
                hasOpenAi ? palette.PromptFg.AnsiFg : palette.SecondaryText.AnsiFg,
                hasOpenAi ? "Change" : "Set up",
                helperText: "Powers AI layout and article extraction · narration uses audio-engine's own keys");

            RenderRow(
                helpers,
                palette,
                width,
                rows,
                selectedIndex,
                SetupRow.LayoutModel,
                layoutIsLocal ? "◐" : "●",
                palette.PromptFg.AnsiFg,
                "Layout model",
                layoutDisplay,
                palette.PromptFg.AnsiFg,
                "Change",
                helperText: "Judge for AI link layout · Local runs a small VLM via Ollama (capable machines only)");

            // ---- Podcast narration (workspace-2xej.7) ----
            helpers.WriteLine();
            helpers.WriteLine($"  {palette.SecondaryText.AnsiFg}Podcast — narration{Reset}");
            RenderRow(
                helpers,
                palette,
                width,
                rows,
                selectedIndex,
                SetupRow.AudioEngine,
                audioEngineReady ? "●" : "○",
                audioEngineReady ? palette.PromptFg.AnsiFg : palette.SecondaryText.AnsiFg,
                "Audio engine",
                audioEngineValue,
                audioEngineReady ? palette.PromptFg.AnsiFg : palette.SecondaryText.AnsiFg,
                "Change",
                helperText: "audio-engine makes the audio. Run it locally (see its README) — default http://127.0.0.1:8080",
                isWarning: !audioEngineReady);

            RenderRow(
                helpers,
                palette,
                width,
                rows,
                selectedIndex,
                SetupRow.AudioEngineProgram,
                programFound ? "●" : "○",
                programFound ? palette.PromptFg.AnsiFg : palette.SecondaryText.AnsiFg,
                "Audio engine program",
                programValue,
                programFound ? palette.PromptFg.AnsiFg : palette.SecondaryText.AnsiFg,
                programFound ? "Change" : "Set",
                helperText: "Started for you when nothing answers at the URL above · folder or executable");

            RenderRow(
                helpers,
                palette,
                width,
                rows,
                selectedIndex,
                SetupRow.AudioEngineKey,
                hasAudioEngineKey ? "●" : "○",
                hasAudioEngineKey ? palette.PromptFg.AnsiFg : palette.SecondaryText.AnsiFg,
                "Audio engine key",
                audioEngineKeyValue,
                hasAudioEngineKey ? palette.PromptFg.AnsiFg : palette.SecondaryText.AnsiFg,
                hasAudioEngineKey ? "Change" : "Set",
                helperText: "Only needed when audio-engine was started with an API key");

            var engineAnswered = narratorStatus?.Reachable == true;
            var narrationValue = "audio-engine isn't running";
            if (narratorStatus is null)
            {
                narrationValue = "checking…";
            }
            else if (engineAnswered)
            {
                var engineName = string.IsNullOrWhiteSpace(narratorStatus.EngineName) ? "audio-engine's engine" : narratorStatus.EngineName;
                narrationValue = $"{engineName} (chosen in audio-engine)";
            }

            paintedFeedText = DescribePublishing(narratorStatus, defaultFeed);

            RenderRow(
                helpers,
                palette,
                width,
                rows,
                selectedIndex,
                SetupRow.Narration,
                engineAnswered ? "●" : "○",
                engineAnswered ? palette.PromptFg.AnsiFg : palette.SecondaryText.AnsiFg,
                "Narration",
                narrationValue,
                engineAnswered ? palette.PromptFg.AnsiFg : palette.SecondaryText.AnsiFg,
                "Open",
                helperText: "Engine and voice are chosen in audio-engine. Enter opens audio-engine.");

            var publishing = defaultFeed is not null;
            RenderRow(
                helpers,
                palette,
                width,
                rows,
                selectedIndex,
                SetupRow.Publishing,
                publishing ? "●" : "○",
                publishing ? palette.PromptFg.AnsiFg : palette.SecondaryText.AnsiFg,
                "Publishing",
                paintedFeedText,
                publishing ? palette.PromptFg.AnsiFg : palette.SecondaryText.AnsiFg,
                "Open",
                helperText: "Podcast feeds are set up in audio-engine: Settings, Podcast feeds, Default feed. Enter opens audio-engine.");

            // ---- Podcast output ----
            helpers.WriteLine();
            helpers.WriteLine($"  {palette.SecondaryText.AnsiFg}Podcast — output{Reset}");
            var folderDisplay = TruncateMiddle(outputFolder, Math.Max(20, width - 40));
            RenderRow(
                helpers,
                palette,
                width,
                rows,
                selectedIndex,
                SetupRow.OutputFolder,
                "●",
                palette.PromptFg.AnsiFg,
                "Output folder",
                folderDisplay,
                palette.PromptFg.AnsiFg,
                "Change");

            RenderRow(
                helpers,
                palette,
                width,
                rows,
                selectedIndex,
                SetupRow.AutoPurgeHours,
                "●",
                palette.PromptFg.AnsiFg,
                "Auto-purge window",
                $"{purgeHours}h",
                palette.PromptFg.AnsiFg,
                "Change",
                helperText: "Output files older than this are auto-deleted on app start");

            // workspace-sakf: surface the hidden PodcastCostGateAlwaysShow toggle
            // so users can find it without hand-editing settings.json. Prior to
            // this row it was an undiscoverable knob set during test scaffolding.
            // Label kept ≤24 chars to fit SettingsRowRenderer's label column.
            RenderRow(
                helpers,
                palette,
                width,
                rows,
                selectedIndex,
                SetupRow.CostGateAlwaysShow,
                "●",
                palette.PromptFg.AnsiFg,
                "Always confirm cost",
                costGateAlwaysShow ? "on" : "off",
                costGateAlwaysShow ? palette.PromptFg.AnsiFg : palette.SecondaryText.AnsiFg,
                "Toggle",
                helperText: "Pop the cost-gate modal even when the spend is below threshold");

            // ---- Browser (workspace-m7aml.3) ----
            helpers.WriteLine();
            helpers.WriteLine($"  {palette.SecondaryText.AnsiFg}Browser{Reset}");
            var extensionValue = BrowserExtensionCard.RowSummary(
                BrowserExtensionCard.CachedRegistrations ?? Array.Empty<BrowserExtension.BrowserRegistration>(),
                BrowserExtension.BrowserConnections.Read(BrowserExtension.BrowserExtensionPaths.ConnectionsPath()));
            var extensionConnected = extensionValue != "Not set up";
            RenderRow(
                helpers,
                palette,
                width,
                rows,
                selectedIndex,
                SetupRow.BrowserExtension,
                extensionConnected ? "●" : "○",
                extensionConnected ? palette.PromptFg.AnsiFg : palette.SecondaryText.AnsiFg,
                "Browser extension",
                extensionValue,
                extensionConnected ? palette.PromptFg.AnsiFg : palette.SecondaryText.AnsiFg,
                "Set up",
                helperText: "Save the article you're reading to your Reading List from Chrome, Vivaldi, Brave or Edge");

            // workspace-rzz8o.1: paint the captured body through a window that leaves room for
            // the footer (blank + hint bar = 2 lines). A "▲ more" / "▼ more" marker takes a
            // line at whichever edge has hidden rows; the offset is settled in a few passes
            // because the marker set depends on the offset it produces.
            var body = helpers.EndCapture();
            var budget = Math.Max(1, options.TerminalHeight - helpers.LinesWritten - 2);
            var total = body.Count;
            if (total <= budget)
            {
                bodyOffset = 0;
                foreach (var line in body)
                {
                    helpers.WriteLine(line);
                }
            }
            else
            {
                var focus = helpers.CapturedFocus;
                var window = budget;
                var offset = Math.Clamp(bodyOffset, 0, Math.Max(0, total - window));
                var topMarker = false;
                var bottomMarker = false;
                for (var pass = 0; pass < 3; pass++)
                {
                    topMarker = offset > 0;
                    bottomMarker = offset + window < total;
                    window = Math.Max(1, budget - (topMarker ? 1 : 0) - (bottomMarker ? 1 : 0));
                    if (focus is { } f)
                    {
                        if (f.Start < offset)
                        {
                            // Keep one line of context (usually the section header) above the
                            // row, and snap to the very top when only a line or two would hide.
                            offset = f.Start - 1 <= 2 ? 0 : f.Start - 1;
                        }
                        else if (f.End >= offset + window)
                        {
                            offset = f.End - window + 1;
                        }
                    }

                    offset = Math.Clamp(offset, 0, Math.Max(0, total - window));
                }

                bodyOffset = offset;
                var dim = palette.GetDimFg().AnsiFg;
                if (topMarker)
                {
                    helpers.WriteLine($"  {dim}▲ more{Reset}");
                }

                var end = Math.Min(total, offset + window);
                for (var i = offset; i < end; i++)
                {
                    helpers.WriteLine(body[i]);
                }

                if (bottomMarker)
                {
                    helpers.WriteLine($"  {dim}▼ more{Reset}");
                }
            }

            // Bottom hint bar
            helpers.WriteLine();
            helpers.WriteLine(
                $"  {palette.GetAccentFg().AnsiFg}↑↓{Reset}{palette.GetDimFg().AnsiFg}:navigate   " +
                $"{palette.GetAccentFg().AnsiFg}Enter{Reset}{palette.GetDimFg().AnsiFg}:edit   " +
                $"{palette.GetAccentFg().AnsiFg}Esc{Reset}{palette.GetDimFg().AnsiFg}:back   " +
                $"{palette.GetAccentFg().AnsiFg}:{Reset}{palette.GetDimFg().AnsiFg}:run command{Reset}");
            helpers.ClearRemainingLines();

            // workspace-c3ec4.6: while the screen idles, re-probe audio-engine every
            // NarratorStatusRefresh so starting or stopping it shows up without a keypress.
            pendingInput ??= ctx.InputHandler.WaitForInputAsync(ct);
            var statusChanged = false;
            while (!pendingInput.IsCompleted && !ct.IsCancellationRequested)
            {
                var finished = await Task.WhenAny(pendingInput, Task.Delay(NarratorStatusRefresh, ct)).ConfigureAwait(false);
                if (finished == pendingInput)
                {
                    break;
                }

                var probed = await ProbeNarratorAsync(scope, ct).ConfigureAwait(false);
                if (probed is null)
                {
                    continue;
                }

                var probedFeed = probed.Reachable ? await ProbeDefaultFeedAsync(scope, ct).ConfigureAwait(false) : null;
                if (DescribeNarratorStatus(probed, launcher.IsStarting(probed.Url)) != paintedNarratorText
                    || DescribePublishing(probed, probedFeed) != paintedFeedText)
                {
                    narratorStatus = probed;
                    defaultFeed = probedFeed;
                    statusChanged = true;
                    break;
                }
            }

            if (statusChanged)
            {
                continue;
            }

            var command = await pendingInput.ConfigureAwait(false);
            pendingInput = null;

            if (command.Type == CommandType.TerminalResized)
            {
                options = ctx.GetCurrentRenderOptions();
                continue;
            }

            if (command.Type is CommandType.GoBack or CommandType.Quit)
            {
                break;
            }

            if (command.Type == CommandType.MoveDown)
            {
                selectedIndex = (selectedIndex + 1) % rows.Length;
                continue;
            }

            if (command.Type == CommandType.MoveUp)
            {
                selectedIndex = (selectedIndex - 1 + rows.Length) % rows.Length;
                continue;
            }

            if (command.Type == CommandType.OpenCommandLine)
            {
                var input = await ctx.InputHandler.PromptForInputAsync(":", ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(input))
                {
                    await SearchCommandHandler.HandleCommandLineInput(ctx, input.Trim(), options, ct).ConfigureAwait(false);
                }

                continue;
            }

            if (command.Type == CommandType.ActivateLink)
            {
                // After the first interactive edit the welcome banner has done its
                // job — drop it so the screen reads as a regular settings page.
                showWelcomeBanner = false;
                var dispatched = rows[selectedIndex];
                await DispatchRowAsync(ctx, options, dispatched, ct).ConfigureAwait(false);

                if (dispatched is SetupRow.AudioEngine or SetupRow.AudioEngineProgram or SetupRow.AudioEngineKey)
                {
                    narratorStatusFresh = false;
                }

                continue;
            }
        }

        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Routes Enter on a Setup row to the matching prompt/picker. Exposed
    /// internally so unit tests can drive the dispatch table without touching
    /// the render loop.
    /// </summary>
    internal static async Task DispatchRowAsync(
        CommandContext ctx,
        RenderOptions options,
        SetupRow row,
        CancellationToken ct)
    {
        switch (row)
        {
            case SetupRow.OpenAiKey:
                await HandleSetApiKey(ctx, options, ct).ConfigureAwait(false);
                return;
            case SetupRow.LayoutModel:
                await HandleSetLayoutModel(ctx, options, ct).ConfigureAwait(false);
                return;
            case SetupRow.OutputFolder:
            {
                using var scope = ctx.ScopeFactory.CreateScope();
                var store = scope.ServiceProvider.GetRequiredService<IUserSettingsStore>();
                var current = store.Get(KeyPodcastOutputFolder) ?? ResolveDefaultOutputFolder();
                await PodcastSetupHelpers.PromptAndSetOutputFolderAsync(
                    ctx, store, current, ct).ConfigureAwait(false);
                return;
            }

            case SetupRow.AutoPurgeHours:
                await HandleSetAutoPurgeHours(ctx, options, ct).ConfigureAwait(false);
                return;

            case SetupRow.CostGateAlwaysShow:
                HandleToggleCostGateAlwaysShow(ctx);
                return;

            case SetupRow.AudioEngine:
                await HandleSetAudioEngineUrlAsync(ctx, options, ct).ConfigureAwait(false);
                return;

            case SetupRow.AudioEngineProgram:
                await HandleSetAudioEngineProgramAsync(ctx, options, ct).ConfigureAwait(false);
                return;

            case SetupRow.AudioEngineKey:
                await HandleSetAudioEngineKeyAsync(ctx, options, ct).ConfigureAwait(false);
                return;

            case SetupRow.Narration:
            case SetupRow.Publishing:
                OpenAudioEngine(ctx);
                return;

            case SetupRow.BrowserExtension:
                await BrowserExtensionCard.RunAsync(ctx, options, ct).ConfigureAwait(false);
                return;
        }
    }

    /// <summary>
    /// The Setup screen's row list — credentials first, then the narration section
    /// (audio-engine rows only; engine and voice are chosen in audio-engine), then the
    /// output rows.
    /// </summary>
    internal static SetupRow[] BuildSetupRows()
    {
        var rowList = new List<SetupRow>
        {
            SetupRow.OpenAiKey,
            SetupRow.LayoutModel,
            SetupRow.AudioEngine,
            SetupRow.AudioEngineProgram,
            SetupRow.AudioEngineKey,
            SetupRow.Narration,
            SetupRow.Publishing,
        };
        rowList.AddRange(new[]
        {
            SetupRow.OutputFolder, SetupRow.AutoPurgeHours, SetupRow.CostGateAlwaysShow, SetupRow.BrowserExtension,
        });
        return [.. rowList];
    }

    /// <summary>
    /// Short status word for the Audio engine row (workspace-c3ec4.3).
    /// </summary>
    /// <param name="status">The last probe; null while none has completed.</param>
    /// <param name="isStarting">True while WireCopy is starting audio-engine itself (workspace-08e4u).</param>
    /// <returns>A few words the row shows after the URL.</returns>
    internal static string DescribeNarratorStatus(NarratorStatus? status, bool isStarting = false)
    {
        if (status is null)
        {
            return "checking…";
        }

        if (status.Ready)
        {
            return "ready ✓";
        }

        return status.ErrorKind switch
        {
            NarrationErrorKind.Unreachable => isStarting ? "starting…" : "not running",
            NarrationErrorKind.Unauthorized => "key rejected",
            NarrationErrorKind.NotConfigured => "no key",
            NarrationErrorKind.EngineNotConfigured => "engine not configured",
            _ => string.IsNullOrWhiteSpace(status.Message) ? "not ready" : status.Message,
        };
    }

    /// <summary>
    /// The audio-engine URL supplied by <c>AUDIO_ENGINE_URL</c>, or null when unset — the
    /// row shows the URL narration actually talks to (mirrors AudioEngineSettings.Resolve).
    /// </summary>
    /// <returns>The trimmed environment URL, or null.</returns>
    internal static string? AudioEngineUrlFromEnv()
    {
        var value = Environment.GetEnvironmentVariable(AudioEngineSettings.UrlEnvVar);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>
    /// Validates the Audio engine URL field: must be an absolute http/https URL.
    /// </summary>
    /// <param name="value">The raw field text.</param>
    /// <returns>An error message, or null when valid.</returns>
    internal static string? ValidateAudioEngineUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "URL cannot be empty";
        }

        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return "Enter an absolute http:// or https:// URL (e.g. http://127.0.0.1:8080)";
        }

        return null;
    }

    /// <summary>The Publishing row's value for the last probes.</summary>
    /// <param name="status">The last status probe; null while none has completed.</param>
    /// <param name="feed">audio-engine's default feed, or null.</param>
    /// <returns>The words the row shows.</returns>
    private static string DescribePublishing(NarratorStatus? status, NarrationFeed? feed)
    {
        if (status is null)
        {
            return "checking…";
        }

        if (!status.Reachable)
        {
            return "audio-engine isn't running";
        }

        if (feed is null)
        {
            return "not published (no default feed in audio-engine)";
        }

        var title = string.IsNullOrWhiteSpace(feed.Title) ? feed.Name : feed.Title;
        return $"to {title}";
    }

    /// <summary>One bounded audio-engine default-feed probe; null when there is none or it times out.</summary>
    private static async Task<NarrationFeed?> ProbeDefaultFeedAsync(IServiceScope scope, CancellationToken ct)
    {
        try
        {
            var narrator = scope.ServiceProvider.GetRequiredService<IPodcastNarrator>();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(NarratorProbeTimeout);
            return await narrator.GetDefaultFeedAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Broad by design: a feed probe must never abort the Setup screen.
            return null;
        }
    }

    /// <summary>Enter on the Narration / Publishing rows: opens audio-engine's web UI in the default browser.</summary>
    private static void OpenAudioEngine(CommandContext ctx)
    {
        using var scope = ctx.ScopeFactory.CreateScope();
        var settingsStore = scope.ServiceProvider.GetRequiredService<IUserSettingsStore>();
        var url = settingsStore.Get(KeyAudioEngineUrl) ?? AudioEngineUrlFromEnv() ?? ResolveTtsDefault(scope, c => c.Url, "http://127.0.0.1:8080");
        if (OpenExternal(url, ctx.Logger))
        {
            ctx.NavigationService.SetStatusMessage($"Opened audio-engine at {url}");
        }
        else
        {
            ctx.NavigationService.SetStatusMessage($"Couldn't open a browser. audio-engine is at {url}", StatusSeverity.Error);
        }
    }

    private static bool OpenExternal(string url, ILogger logger)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
                return true;
            }

            var psi = new System.Diagnostics.ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add(url);
            System.Diagnostics.Process.Start(psi)?.Dispose();
            return true;
        }
        catch (Exception ex)
        {
            logger.LogInformation(ex, "No browser available to open {Url}", url);
            return false;
        }
    }

    /// <summary>
    /// One bounded (<see cref="NarratorProbeTimeout"/>) audio-engine status probe; null
    /// when the probe times out or throws so the screen degrades to "checking…".
    /// </summary>
    private static async Task<NarratorStatus?> ProbeNarratorAsync(IServiceScope scope, CancellationToken ct)
    {
        try
        {
            var narrator = scope.ServiceProvider.GetRequiredService<IPodcastNarrator>();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(NarratorProbeTimeout);
            return await narrator.CheckAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception)
        {
            // Broad by design: a status probe must never abort the Setup screen.
            return null;
        }
    }

    /// <summary>
    /// Toggles <see cref="KeyPodcastCostGateAlwaysShow"/> between true and false
    /// (workspace-sakf). Enter on the Setup row flips the value and writes it
    /// straight through <see cref="IUserSettingsStore"/> — no input prompt
    /// needed for a binary preference.
    /// </summary>
    private static void HandleToggleCostGateAlwaysShow(CommandContext ctx)
    {
        try
        {
            using var scope = ctx.ScopeFactory.CreateScope();
            var settingsStore = scope.ServiceProvider.GetRequiredService<IUserSettingsStore>();
            var current = bool.TryParse(
                settingsStore.Get(KeyPodcastCostGateAlwaysShow), out var parsed) && parsed;
            var next = !current;
            settingsStore.Set(
                KeyPodcastCostGateAlwaysShow,
                next ? "true" : "false");
            ctx.NavigationService.SetStatusMessage(
                next
                    ? "Cost-gate will pop on every generate"
                    : "Cost-gate will only pop above threshold");
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Failed to toggle PodcastCostGateAlwaysShow");
            ctx.NavigationService.SetStatusMessage("Failed to update cost-gate setting", StatusSeverity.Error);
        }
    }

#pragma warning disable SA1201, SA1202 // helpers grouped near their callers for readability

    private static void RenderRow(
        RenderHelpers helpers,
        ThemePalette palette,
        int width,
        SetupRow[] rows,
        int selectedIndex,
        SetupRow rowId,
        string statusIcon,
        string statusColor,
        string label,
        string value,
        string valueColor,
        string actionLabel,
        string? helperText = null,
        string? warningText = null,
        bool isWarning = false)
    {
        var isSelected = rows[selectedIndex] == rowId;
        var (mainLine, subLine) = SettingsRowRenderer.Build(
            palette,
            width,
            isSelected,
            isWarning,
            statusIcon,
            statusColor,
            label,
            value,
            valueColor,
            actionLabel,
            warningText,
            helperText);
        var firstLine = helpers.CapturedLineCount;
        helpers.WriteLine(mainLine);
        if (subLine != null)
        {
            helpers.WriteLine(subLine);
        }

        if (isSelected)
        {
            // workspace-rzz8o.1: tell the window painter which captured lines must stay visible.
            helpers.CapturedFocus = (firstLine, helpers.CapturedLineCount - 1);
        }
    }

    private static string ResolveDefaultOutputFolder() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WireCopy",
            "output");

    /// <summary>
    /// Resolves a narration configuration default from the bound
    /// <see cref="AudioEngineConfiguration"/> when the settings store has no
    /// override. Falls back to the supplied literal when no options are
    /// registered (defensive: keeps the Setup screen renderable in the unit
    /// tests' bare DI container).
    /// </summary>
    private static string ResolveTtsDefault(
        IServiceScope scope,
        Func<AudioEngineConfiguration, string> selector,
        string fallback)
    {
        try
        {
            var opts = scope.ServiceProvider.GetService<IOptions<AudioEngineConfiguration>>();
            var value = opts is null ? null : selector(opts.Value);
            return string.IsNullOrEmpty(value) ? fallback : value;
        }
        catch
        {
            // Broad by design (workspace-3v8z). Resolves a config display value and must
            // degrade to the fallback on any options or DI failure. Narrowing risks aborting
            // the settings render on an unexpected exception type. No logger in this helper.
            return fallback;
        }
    }

    private static int ResolvePurgeHours(IServiceScope scope, IUserSettingsStore store)
    {
        var saved = store.Get(KeyOutputRetentionHours);
        if (!string.IsNullOrWhiteSpace(saved) && int.TryParse(saved, out var parsed) && parsed > 0)
        {
            return parsed;
        }

        try
        {
            var opts = scope.ServiceProvider.GetService<IOptions<PodcastConfiguration>>();
            return opts?.Value.OutputRetentionHours ?? 36;
        }
        catch
        {
            // Broad by design (workspace-3v8z). A config read for the retention default that
            // must degrade to 36 hours on any options validation or DI failure rather than
            // throwing out of the settings screen. No logger is available in this helper.
            return 36;
        }
    }

    /// <summary>
    /// Truncates the middle of a long path with an ellipsis so both ends stay
    /// visible (mirrors the helper in <see cref="PodcastSetupHelpers"/>).
    /// </summary>
    private static string TruncateMiddle(string text, int maxWidth)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxWidth || maxWidth <= 1)
        {
            return text;
        }

        if (maxWidth <= 3)
        {
            return new string('.', maxWidth);
        }

        var keep = maxWidth - 1;
        var leftKeep = (keep + 1) / 2;
        var rightKeep = keep - leftKeep;
        return text[..leftKeep] + "…" + text[^rightKeep..];
    }

    internal static async Task HandleSetCommand(CommandContext ctx, string? subcommand, RenderOptions options, CancellationToken ct)
    {
        var setting = subcommand?.Trim().ToLowerInvariant();

        switch (setting)
        {
            case "apikey":
                await HandleSetApiKey(ctx, options, ct).ConfigureAwait(false);
                break;

            default:
                ctx.NavigationService.SetStatusMessage("Usage: :set apikey");
                await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
                break;
        }
    }

    internal static async Task HandleClearCommand(CommandContext ctx, string? subcommand, RenderOptions options, CancellationToken ct)
    {
        var setting = subcommand?.Trim().ToLowerInvariant();

        switch (setting)
        {
            case "apikey":
                await HandleClearApiKey(ctx, options, ct).ConfigureAwait(false);
                break;

            default:
                // No recognized subcommand — delegate to existing collection clear behavior
                await CollectionCommandHandler.HandleClearCollection(ctx, options, ct).ConfigureAwait(false);
                break;
        }
    }

    private static Task HandleSetApiKey(CommandContext ctx, RenderOptions options, CancellationToken ct)
        => HandleSetApiKey(ctx, options, ct, subtitle: null, resumeAfterSave: null);

    /// <summary>
    /// Overload that lets callers (e.g. <see cref="PodcastCommandHandler.RunGeneratePodcastAttempt"/>)
    /// deep-link into the API-key prompt and resume their flow after a
    /// successful save (workspace-yib5, Phase 5 of workspace-mhwa).
    /// </summary>
    /// <param name="subtitle">
    /// Optional one-line subtitle rendered above the input box. Used by the
    /// generate-podcast resume path to surface "Set this up and we'll
    /// continue generating your podcast" so the user understands why they're
    /// here.
    /// </param>
    /// <param name="resumeAfterSave">
    /// Optional callback invoked when the user saves a valid key. When set
    /// and the auth probe passes, it fires before this method returns —
    /// callers use it to re-enter their flow without forcing the user to
    /// press p again. Skipped on Esc, save failure, or auth probe failure.
    /// </param>
    internal static async Task HandleSetApiKey(
        CommandContext ctx,
        RenderOptions options,
        CancellationToken ct,
        string? subtitle,
        Func<Task>? resumeAfterSave)
    {
        var palette = BuiltInThemes.Get(ctx.ThemeProvider.CurrentTheme);
        var field = new FormFieldConfig
        {
            Label = "OpenAI API Key",
            Subtitle = subtitle,
            Placeholder = "sk-...",
            HelpText = "Get a key at platform.openai.com/api-keys",
            IsSecret = true,
            Validate = v => string.IsNullOrWhiteSpace(v) ? "Key cannot be empty" : null,
        };

        var startRow = Math.Max(1, (Console.WindowHeight / 2) - 3);
        var fieldWidth = Math.Min(UI.OverlayViewport.Width - 6, 60);
        var apiKey = await FormField.PromptAsync(ctx.InputHandler, field, palette, startRow, fieldWidth, ct).ConfigureAwait(false);

        if (apiKey == null)
        {
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
            return;
        }

        var trimmedKey = apiKey.Trim();

        using var scope = ctx.ScopeFactory.CreateScope();

        ctx.NavigationService.SetStatusMessage("Verifying API key...");
        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);

        var validation = await new OpenAiKeyValidator().ValidateAsync(trimmedKey, ct).ConfigureAwait(false);

        if (validation.IsValid)
        {
            try
            {
                var settingsStore = scope.ServiceProvider.GetRequiredService<IUserSettingsStore>();
                settingsStore.Set(KeyOpenAiApiKey, trimmedKey, encrypt: true);
                ctx.NavigationService.SetStatusMessage("API key saved");
            }
            catch (Exception ex)
            {
                ctx.Logger.LogWarning(ex, "Failed to persist API key");
                ctx.NavigationService.SetStatusMessage("API key verified but failed to save", StatusSeverity.Error);
            }

            if (resumeAfterSave != null)
            {
                await resumeAfterSave().ConfigureAwait(false);
                return;
            }
        }
        else
        {
            ctx.NavigationService.SetStatusMessage(validation.ErrorMessage ?? "Invalid API key", StatusSeverity.Error);
        }

        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// workspace-r8on: switch the AI-layout JUDGE between the OpenAI API
    /// (gpt-5-nano, default) and a local Ollama VLM. Type a model name to go local
    /// (sets the Ollama endpoint + a dummy key); 'openai' to return to the API;
    /// blank keeps the current choice. Only the small judge runs locally —
    /// selectors are code and article extraction stays on the API.
    /// </summary>
    private static async Task HandleSetLayoutModel(
        CommandContext ctx,
        RenderOptions options,
        CancellationToken ct)
    {
        using var scope = ctx.ScopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IUserSettingsStore>();
        var isLocal = !string.IsNullOrWhiteSpace(store.Get(KeyLayoutEndpoint));
        var currentModel = store.Get(KeyLayoutModel) ?? (isLocal ? string.Empty : "gpt-5-nano");

        var prompt = isLocal
            ? $"Layout judge — local Ollama model [{currentModel}] · 'openai' for the API · blank keeps: "
            : "Layout judge — a local Ollama model NAME (e.g. qwen2.5vl) for offline, or 'openai' for the API · blank keeps: ";

        var input = await ctx.InputHandler.PromptForInputAsync(
            prompt, ct, isSecret: false, initialInput: isLocal ? currentModel : string.Empty).ConfigureAwait(false);
        if (input == null)
        {
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
            return;
        }

        var trimmed = input.Trim();
        if (trimmed.Length == 0)
        {
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
            return; // keep current
        }

        if (trimmed.Equals("openai", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("api", StringComparison.OrdinalIgnoreCase))
        {
            store.Remove(KeyLayoutEndpoint);
            store.Remove(KeyLayoutApiKey);
            store.Remove(KeyLayoutModel);
            ctx.NavigationService.SetStatusMessage("Layout model → OpenAI gpt-5-nano");
        }
        else
        {
            store.Set(KeyLayoutEndpoint, DefaultOllamaEndpoint);
            store.Set(KeyLayoutApiKey, "ollama");
            store.Set(KeyLayoutModel, trimmed);
            ctx.NavigationService.SetStatusMessage(
                $"Layout judge → LOCAL {trimmed} via Ollama — needs a capable machine and `ollama pull {trimmed}`",
                StatusSeverity.Info);
        }

        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
    }

    /// <summary>"just now" / "5m ago" / "2h ago" / "3d ago" for the tested-at stamp.</summary>
    internal static string FormatRelativeTime(TimeSpan age)
    {
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        if (age.TotalMinutes < 1)
        {
            return "just now";
        }

        if (age.TotalHours < 1)
        {
            return $"{(int)age.TotalMinutes}m ago";
        }

        return age.TotalDays < 1 ? $"{(int)age.TotalHours}h ago" : $"{(int)age.TotalDays}d ago";
    }

    /// <summary>
    /// Prompts for the auto-purge retention window in hours and persists it to
    /// the settings store. Accepts any positive integer; "reset" / "default"
    /// reverts to the configured default.
    /// </summary>
    private static async Task HandleSetAutoPurgeHours(
        CommandContext ctx,
        RenderOptions options,
        CancellationToken ct)
    {
        var input = await ctx.InputHandler.PromptForInputAsync(
            "Auto-purge window in hours (positive integer, default 36, 'reset' to revert): ",
            ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(input))
        {
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
            return;
        }

        var trimmed = input.Trim();
        using var scope = ctx.ScopeFactory.CreateScope();
        var settingsStore = scope.ServiceProvider.GetRequiredService<IUserSettingsStore>();

        if (trimmed.Equals("reset", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                settingsStore.Remove(KeyOutputRetentionHours);
                ctx.NavigationService.SetStatusMessage("Auto-purge window reset to default");
            }
            catch (Exception ex)
            {
                ctx.Logger.LogWarning(ex, "Failed to clear auto-purge override");
                ctx.NavigationService.SetStatusMessage("Failed to reset auto-purge window", StatusSeverity.Error);
            }

            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
            return;
        }

        if (!int.TryParse(trimmed, out var hours) || hours <= 0)
        {
            ctx.NavigationService.SetStatusMessage(
                $"Invalid: \"{trimmed}\" — enter a positive integer number of hours");
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
            return;
        }

        try
        {
            settingsStore.Set(KeyOutputRetentionHours, hours.ToString(System.Globalization.CultureInfo.InvariantCulture));
            ctx.NavigationService.SetStatusMessage($"Auto-purge set to {hours}h");
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Failed to persist auto-purge window");
            ctx.NavigationService.SetStatusMessage("Failed to save auto-purge window", StatusSeverity.Error);
        }

        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Enter on the Audio engine row (workspace-c3ec4.3): prompts for the URL audio-engine
    /// listens on and saves it plain; the caller re-probes status afterwards.
    /// </summary>
    private static async Task HandleSetAudioEngineUrlAsync(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        using var scope = ctx.ScopeFactory.CreateScope();
        var settingsStore = scope.ServiceProvider.GetRequiredService<IUserSettingsStore>();
        var current = settingsStore.Get(KeyAudioEngineUrl) ?? AudioEngineUrlFromEnv() ?? ResolveTtsDefault(scope, c => c.Url, "http://127.0.0.1:8080");
        var palette = BuiltInThemes.Get(ctx.ThemeProvider.CurrentTheme);
        var field = new FormFieldConfig
        {
            Label = "Audio engine URL",
            Placeholder = "http://127.0.0.1:8080",
            HelpText = "Where audio-engine is listening · Enter to save · Esc to cancel",
            IsSecret = false,
            MaxLength = 200,
            InitialValue = current,
            Validate = ValidateAudioEngineUrl,
        };

        var startRow = Math.Max(1, (Console.WindowHeight / 2) - 3);
        var fieldWidth = Math.Min(UI.OverlayViewport.Width - 6, 60);
        var input = await FormField.PromptAsync(ctx.InputHandler, field, palette, startRow, fieldWidth, ct).ConfigureAwait(false);
        if (input is null)
        {
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
            return;
        }

        var trimmed = input.Trim();
        try
        {
            settingsStore.Set(KeyAudioEngineUrl, trimmed);
            ctx.NavigationService.SetStatusMessage($"Audio engine → {trimmed}");
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Failed to save audio-engine URL");
            ctx.NavigationService.SetStatusMessage("Failed to save audio-engine URL", StatusSeverity.Error);
        }

        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
    }

    /// <summary>How the audio-engine location prompt ended.</summary>
    internal enum AudioEngineLocationOutcome
    {
        /// <summary>A location was chosen, verified and saved.</summary>
        Saved,

        /// <summary>The setting was blanked; WireCopy will look for audio-engine itself.</summary>
        Cleared,

        /// <summary>The person backed out without choosing.</summary>
        Cancelled,

        /// <summary>The chosen folder holds no audio-engine; an error status was set.</summary>
        NotAnEngine,
    }

    /// <summary>
    /// Enter on the Audio engine program row (workspace-08e4u): asks where audio-engine is, so
    /// WireCopy can start it when nothing answers at the audio-engine URL.
    /// </summary>
    private static async Task HandleSetAudioEngineProgramAsync(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        _ = await PromptAudioEngineLocationAsync(ctx, ct).ConfigureAwait(false);
        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Asks where audio-engine lives: the shell's native folder picker when one is attached,
    /// otherwise (or when the picker is dismissed) a guided typed prompt. Saves the location the
    /// launcher resolved (an absolute folder or executable, never the raw text), resets the
    /// launcher's attempt guard and sets the status line. Does not redraw the page.
    /// </summary>
    /// <param name="ctx">The command context.</param>
    /// <param name="ct">Cancellation.</param>
    /// <param name="setStatus">Called with the status text just before the native dialog opens and with null when the typed prompt is shown; null for none.</param>
    /// <returns>How the prompt ended, plus the error text when it was <see cref="AudioEngineLocationOutcome.NotAnEngine"/>.</returns>
    internal static async Task<(AudioEngineLocationOutcome Outcome, string? Message)> PromptAudioEngineLocationAsync(CommandContext ctx, CancellationToken ct, Action<string?>? setStatus = null)
    {
        using var scope = ctx.ScopeFactory.CreateScope();
        var settingsStore = scope.ServiceProvider.GetRequiredService<IUserSettingsStore>();
        var launcher = scope.ServiceProvider.GetRequiredService<IAudioEngineLauncher>();
        var shell = scope.ServiceProvider.GetService<IShellChannel>();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var current = settingsStore.Get(KeyAudioEngineProgram) ?? string.Empty;

        if (shell is { IsConnected: true })
        {
            var currentPath = current.Length > 0
                ? AudioEnginePathInput.Normalize(current, home, p => File.Exists(p) || Directory.Exists(p))
                : string.Empty;
            var start = home;
            if (currentPath.Length > 0 && Directory.Exists(currentPath))
            {
                start = currentPath;
            }
            else if (currentPath.Length > 0 && File.Exists(currentPath))
            {
                start = Path.GetDirectoryName(currentPath) ?? home;
            }

            setStatus?.Invoke(RoutineText.ChooseAudioEngineFolder);
            var picked = await shell.PickPathAsync(
                new ShellPickRequest(
                    "Choose the audio-engine folder",
                    "Choose the folder audio-engine is in — the one that holds AudioEngine.Server.",
                    "Use this folder",
                    start,
                    "folder"),
                ct).ConfigureAwait(false);
            if (picked is { Canceled: false, Path: { Length: > 0 } chosen })
            {
                var program = launcher.Probe(chosen);
                if (program is null)
                {
                    var error = $"No audio-engine in {ShortenHome(chosen, home)}. Choose the folder that holds AudioEngine.Server.";
                    ctx.NavigationService.SetStatusMessage(error, StatusSeverity.Error);
                    return (AudioEngineLocationOutcome.NotAnEngine, error);
                }

                return (SaveAudioEngineLocation(ctx, settingsStore, launcher, chosen, program, home), null);
            }
        }

        setStatus?.Invoke(null);
        var field = new FormFieldConfig
        {
            Label = "Where is audio-engine on this computer?",
            Placeholder = "~/audio-engine",
            HelpText = "audio-engine is the separate program that reads articles aloud. Type or paste the folder it is in — the one that holds AudioEngine.Server.\n"
                + "Paste the path exactly as you copied it: spaces are fine, quotes and backslashes are cleaned up for you, and ~ means your home folder.\n"
                + "Enter saves and starts it · leave blank to let WireCopy look for it · Esc cancels",
            IsSecret = false,
            MaxLength = 400,
            InitialValue = current,
            Validate = v => ValidateAudioEngineProgram(v)
                ?? (string.IsNullOrWhiteSpace(v) || launcher.Probe(v) is not null
                    ? null
                    : "No audio-engine there — choose the folder that holds AudioEngine.Server"),
        };

        var palette = BuiltInThemes.Get(ctx.ThemeProvider.CurrentTheme);
        var startRow = Math.Max(1, (Console.WindowHeight / 2) - 3);
        var fieldWidth = Math.Max(20, UI.OverlayViewport.Width - 6);
        var input = await FormField.PromptAsync(ctx.InputHandler, field, palette, startRow, fieldWidth, ct).ConfigureAwait(false);
        if (input is null)
        {
            return (AudioEngineLocationOutcome.Cancelled, null);
        }

        var trimmed = input.Trim();
        if (trimmed.Length == 0)
        {
            return (SaveAudioEngineLocation(ctx, settingsStore, launcher, null, null, home), null);
        }

        var found = launcher.Probe(trimmed);
        if (found is null)
        {
            const string notThere = "No audio-engine there — choose the folder that holds AudioEngine.Server";
            ctx.NavigationService.SetStatusMessage(notThere, StatusSeverity.Error);
            return (AudioEngineLocationOutcome.NotAnEngine, notThere);
        }

        return (SaveAudioEngineLocation(ctx, settingsStore, launcher, trimmed, found, home), null);
    }

    /// <summary>Stores the resolved location (or forgets it when <paramref name="program"/> is null) and sets the status.</summary>
    private static AudioEngineLocationOutcome SaveAudioEngineLocation(
        CommandContext ctx,
        IUserSettingsStore settingsStore,
        IAudioEngineLauncher launcher,
        string? rawPath,
        AudioEngineProgram? program,
        string home)
    {
        try
        {
            if (program is null)
            {
                settingsStore.Remove(KeyAudioEngineProgram);
                launcher.ResetAttempts();
                ctx.NavigationService.SetStatusMessage("audio-engine location → WireCopy will look for it");
                return AudioEngineLocationOutcome.Cleared;
            }

            var normalized = AudioEnginePathInput.Normalize(rawPath ?? string.Empty, home, p => File.Exists(p) || Directory.Exists(p));
            var location = File.Exists(normalized) ? Path.GetFullPath(normalized) : Path.GetFullPath(program.WorkingDirectory);
            settingsStore.Set(KeyAudioEngineProgram, location);
            launcher.ResetAttempts();
            ctx.NavigationService.SetStatusMessage($"audio-engine location → {program.Display}");
            return AudioEngineLocationOutcome.Saved;
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Failed to save audio-engine program");
            ctx.NavigationService.SetStatusMessage("Failed to save audio-engine location", StatusSeverity.Error);
            return AudioEngineLocationOutcome.NotAnEngine;
        }
    }

    /// <summary>Shows <paramref name="path"/> with the home folder as <c>~</c>.</summary>
    private static string ShortenHome(string path, string home)
        => !string.IsNullOrEmpty(home) && path.StartsWith(home, StringComparison.Ordinal) ? "~" + path[home.Length..] : path;

    /// <summary>
    /// Validates the Audio engine program field (workspace-08e4u): blank is allowed (forgets the
    /// setting); otherwise the path, normalised (quotes, <c>file://</c>, backslash escapes, <c>~</c>),
    /// must be an existing file or folder.
    /// </summary>
    /// <param name="value">The typed path.</param>
    /// <returns>Null when acceptable; the problem otherwise.</returns>
    internal static string? ValidateAudioEngineProgram(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string full;
        try
        {
            full = AudioEnginePathInput.Normalize(value, home, p => File.Exists(p) || Directory.Exists(p));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return "Not a valid path";
        }

        return File.Exists(full) || Directory.Exists(full) ? null : $"Not found: {full}";
    }

    /// <summary>
    /// Enter on the Audio engine key row (workspace-c3ec4.3): prompts for audio-engine's
    /// API key, saves it encrypted, then probes audio-engine once and surfaces its message.
    /// </summary>
    private static async Task HandleSetAudioEngineKeyAsync(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        using var scope = ctx.ScopeFactory.CreateScope();
        var settingsStore = scope.ServiceProvider.GetRequiredService<IUserSettingsStore>();
        var palette = BuiltInThemes.Get(ctx.ThemeProvider.CurrentTheme);
        var field = new FormFieldConfig
        {
            Label = "Audio engine API key",
            Placeholder = "the key audio-engine was started with",
            HelpText = "Enter to save · Esc to cancel",
            IsSecret = true,
            Validate = v => string.IsNullOrWhiteSpace(v) ? "Key cannot be empty" : null,
        };

        var startRow = Math.Max(1, (Console.WindowHeight / 2) - 3);
        var fieldWidth = Math.Min(UI.OverlayViewport.Width - 6, 60);
        var input = await FormField.PromptAsync(ctx.InputHandler, field, palette, startRow, fieldWidth, ct).ConfigureAwait(false);
        if (input is null)
        {
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
            return;
        }

        try
        {
            settingsStore.Set(KeyAudioEngineApiKey, input.Trim(), encrypt: true);
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Failed to save audio-engine key");
            ctx.NavigationService.SetStatusMessage("Failed to save audio-engine key", StatusSeverity.Error);
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
            return;
        }

        ctx.NavigationService.SetStatusMessage("Key saved — checking audio-engine…");
        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);

        var status = await ProbeNarratorAsync(scope, ct).ConfigureAwait(false);
        if (status?.Ready == true)
        {
            ctx.NavigationService.SetStatusMessage(string.IsNullOrWhiteSpace(status.Message) ? "audio-engine ready" : status.Message);
        }
        else
        {
            ctx.NavigationService.SetStatusMessage(
                string.IsNullOrWhiteSpace(status?.Message) ? "Key saved — audio-engine not reachable" : status!.Message,
                StatusSeverity.Error);
        }

        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
    }

    private static async Task HandleClearApiKey(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        try
        {
            using var scope = ctx.ScopeFactory.CreateScope();
            var settingsStore = scope.ServiceProvider.GetRequiredService<IUserSettingsStore>();
            settingsStore.Remove(KeyOpenAiApiKey);

            ctx.NavigationService.SetStatusMessage("API key cleared — run :set apikey to restore");
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Failed to remove API key");
            ctx.NavigationService.SetStatusMessage("Failed to clear API key", StatusSeverity.Error);
        }

        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
    }
#pragma warning restore SA1201, SA1202
}
