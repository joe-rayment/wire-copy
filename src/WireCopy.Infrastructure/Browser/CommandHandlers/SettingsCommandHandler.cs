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

namespace WireCopy.Infrastructure.Browser.CommandHandlers;

/// <summary>
/// Handles settings, :set, and :clear commands for API keys, buckets, and configuration.
///
/// The unified Setup screen (workspace-fn1u) extends <see cref="HandleConfigScreen"/>
/// with row-based selection covering every credential and every preference:
/// OpenAI API key (used for both TTS and AI Curated layout — workspace-65sw),
/// GCS service-account key, GCS bucket, podcast output folder, voice, model,
/// and the auto-purge window.
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
    internal const string KeyGcsBucketName = "GcsBucketName";
    internal const string KeyGcsServiceAccountKeyPath = "GcsServiceAccountKeyPath";

    // Display-only cache. Written when the user saves a key; read on every
    // Setup-screen render. Avoids a synchronous File.ReadAllText hit per
    // render. Not encrypted — already-masked text, no secret material.
    internal const string KeyGcsServiceAccountDisplay = "GcsServiceAccountDisplay";
    internal const string KeyPodcastOutputFolder = "PodcastOutputFolder";
    internal const string KeyOpenAiTtsVoice = "OpenAiTtsVoice";
    internal const string KeyOpenAiTtsModel = "OpenAiTtsModel";
    internal const string KeyOpenAiTtsInstructions = "OpenAiTtsInstructions";
    internal const string KeyOutputRetentionHours = "PodcastOutputRetentionHours";
    internal const string KeyPodcastCostGateAlwaysShow = "PodcastCostGateAlwaysShow";

    // workspace-2xej: narration engine selection + Chatterbox (local TTS) knobs.
    internal const string KeyTtsEngine = "TtsEngine";                            // "openai" | "chatterbox"; absent = openai
    internal const string KeyChatterboxVoiceSample = "ChatterboxVoiceSample";    // abs path or repo-relative; absent = built-in voice
    internal const string KeyChatterboxExaggeration = "ChatterboxExaggeration";  // invariant float string
    internal const string KeyChatterboxLastTestOk = "ChatterboxLastTestOk";      // ISO-8601 utc timestamp of last successful Test narration

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

    /// <summary>Rows the inline narration-test panel owns (cleared before drawing and on exit).</summary>
    private const int NarrationTestPanelRows = 7;

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
        GcsKey,
        GcsBucket,
        OutputFolder,
        Voice,
        Model,
        TtsInstructions,
        AutoPurgeHours,
        CostGateAlwaysShow,
        LayoutModel,

        // workspace-c3ec4.3: audio-engine connection rows.
        AudioEngine,
        AudioEngineProgram,  // workspace-08e4u: folder or executable WireCopy starts when nothing answers
        AudioEngineKey,

        // workspace-2xej.7: narration engine picker + Chatterbox (local) rows.
        NarrationEngine,
        ChatterboxSample,
        ChatterboxExaggeration,
        NarrationTest,

        // workspace-m7aml.3: install guide for the browser extension.
        BrowserExtension,
    }

    /// <summary>
    /// First-run detection: returns true when none of the three primary
    /// credentials have been configured. workspace-65sw collapsed the
    /// previous Anthropic-key requirement into the OpenAI key (one credential
    /// now powers both TTS and AI Curated layout), so the predicate covers
    /// OpenAI key + GCS bucket + GCS service-account key.
    /// </summary>
    internal static bool IsFirstRun(IUserSettingsStore settingsStore)
    {
        ArgumentNullException.ThrowIfNull(settingsStore);

        return string.IsNullOrWhiteSpace(settingsStore.Get(KeyOpenAiApiKey))
            && string.IsNullOrWhiteSpace(settingsStore.Get(KeyGcsBucketName))
            && string.IsNullOrWhiteSpace(settingsStore.Get(KeyGcsServiceAccountKeyPath));
    }

    /// <summary>
    /// Returns true when at least one of the three primary credentials is still
    /// unconfigured. Drives the launcher's "press c" Setup hint inside the
    /// header card (workspace-9qzh) — partial-setup users still need a path
    /// into Setup. Distinct from <see cref="IsFirstRun"/>, which is the
    /// stricter "no credential at all" predicate used for the welcome banner.
    /// </summary>
    internal static bool HasIncompleteSetup(IUserSettingsStore settingsStore)
    {
        ArgumentNullException.ThrowIfNull(settingsStore);

        return string.IsNullOrWhiteSpace(settingsStore.Get(KeyOpenAiApiKey))
            || string.IsNullOrWhiteSpace(settingsStore.Get(KeyGcsBucketName))
            || string.IsNullOrWhiteSpace(settingsStore.Get(KeyGcsServiceAccountKeyPath));
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

        // workspace-08e4u: the status words last painted on the Audio engine row, so the 3 s
        // refresh repaints "starting…" → "ready ✓" / "not running" even when the probe looks the same.
        string? paintedNarratorText = null;

        // workspace-c3ec4.6: the input wait outlives a status-driven repaint so a keystroke
        // that lands mid-repaint is still consumed by the next iteration, never dropped.
        Task<NavigationCommand>? pendingInput = null;

        while (!ct.IsCancellationRequested)
        {
            using var scope = ctx.ScopeFactory.CreateScope();
            var settingsStore = scope.ServiceProvider.GetRequiredService<IUserSettingsStore>();
            var launcher = scope.ServiceProvider.GetRequiredService<IAudioEngineLauncher>();
            if (!narratorStatusFresh)
            {
                narratorStatus = await ProbeNarratorAsync(scope, ct).ConfigureAwait(false);
                narratorStatusFresh = true;

                // workspace-08e4u: nothing answers at a local URL — start audio-engine for the user
                // (one automatic attempt per URL per session; the row shows "starting…" meanwhile).
                if (narratorStatus?.ErrorKind == NarrationErrorKind.Unreachable && AudioEngineLauncher.IsLocal(narratorStatus.Url))
                {
                    _ = launcher.BeginStart(narratorStatus.Url);
                }
            }

            // workspace-2xej.7: the row list depends on the active narration engine,
            // so it is rebuilt every iteration — only the active engine's rows show.
            var engineIsChatterbox = string.Equals(
                settingsStore.Get(KeyTtsEngine), "chatterbox", StringComparison.OrdinalIgnoreCase);
            var rows = BuildSetupRows(engineIsChatterbox);
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
            var hasGcsKey = !string.IsNullOrWhiteSpace(settingsStore.Get(KeyGcsServiceAccountKeyPath));
            var gcsKeyDisplay = hasGcsKey ? ResolveGcsKeyDisplay(settingsStore, ctx.Logger) : null;
            var bucketName = settingsStore.Get(KeyGcsBucketName);
            var hasBucket = !string.IsNullOrWhiteSpace(bucketName);
            var outputFolder = settingsStore.Get(KeyPodcastOutputFolder)
                               ?? ResolveDefaultOutputFolder();
            var voice = settingsStore.Get(KeyOpenAiTtsVoice) ?? ResolveTtsDefault(scope, c => c.OpenAiVoice, "nova");
            var model = settingsStore.Get(KeyOpenAiTtsModel) ?? ResolveTtsDefault(scope, c => c.OpenAiModel, "tts-1");
            var instructions = settingsStore.Get(KeyOpenAiTtsInstructions)
                               ?? ResolveTtsDefault(scope, c => c.OpenAiInstructions ?? string.Empty, string.Empty);
            var purgeHours = ResolvePurgeHours(scope, settingsStore);
            var costGateAlwaysShow = bool.TryParse(
                settingsStore.Get(KeyPodcastCostGateAlwaysShow), out var alwaysShowParsed)
                && alwaysShowParsed;

            // workspace-r8on: layout-judge model — OpenAI (default) or a local Ollama.
            var layoutEndpoint = settingsStore.Get(KeyLayoutEndpoint);
            var layoutIsLocal = !string.IsNullOrWhiteSpace(layoutEndpoint);
            var layoutModelName = settingsStore.Get(KeyLayoutModel) ?? "gpt-5-nano";
            var layoutDisplay = layoutIsLocal ? $"Local · {layoutModelName}" : $"OpenAI · {layoutModelName}";

            // workspace-c3ec4.3: Chatterbox row values come straight from the settings store —
            // audio-engine owns the engine; this screen only shows the knobs.
            var samplePath = ResolveChatterboxSamplePath(settingsStore);
            var sampleExists = samplePath is not null && File.Exists(samplePath);
            var exaggerationDisplay = ResolveChatterboxExaggeration(scope, settingsStore)
                .ToString("0.0#", System.Globalization.CultureInfo.InvariantCulture);
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
            string narrationTestValue;
            if (DateTime.TryParse(
                settingsStore.Get(KeyChatterboxLastTestOk),
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var lastTestAt))
            {
                narrationTestValue = $"ready ✓ tested {FormatRelativeTime(DateTime.UtcNow - lastTestAt.ToUniversalTime())}";
            }
            else
            {
                narrationTestValue = "not tested yet";
            }

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

            RenderRow(
                helpers,
                palette,
                width,
                rows,
                selectedIndex,
                SetupRow.GcsKey,
                hasGcsKey ? "●" : "○",
                hasGcsKey ? palette.PromptFg.AnsiFg : palette.SecondaryText.AnsiFg,
                "GCS service account",
                hasGcsKey ? (gcsKeyDisplay ?? "configured") : "not set",
                hasGcsKey ? palette.PromptFg.AnsiFg : palette.SecondaryText.AnsiFg,
                hasGcsKey ? "Change" : "Set up");

            RenderRow(
                helpers,
                palette,
                width,
                rows,
                selectedIndex,
                SetupRow.GcsBucket,
                hasBucket ? "●" : "○",
                hasBucket ? palette.PromptFg.AnsiFg : palette.SecondaryText.AnsiFg,
                "GCS bucket",
                hasBucket ? bucketName! : "not set",
                hasBucket ? palette.PromptFg.AnsiFg : palette.SecondaryText.AnsiFg,
                hasBucket ? "Change" : "Set up");

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

            RenderRow(
                helpers,
                palette,
                width,
                rows,
                selectedIndex,
                SetupRow.NarrationEngine,
                "●",
                palette.PromptFg.AnsiFg,
                "Narration engine",
                engineIsChatterbox ? "Chatterbox (local)" : "OpenAI (cloud)",
                palette.PromptFg.AnsiFg,
                "Change",
                helperText: "Generate audio through audio-engine — OpenAI (cloud) or Chatterbox (local)");

            if (engineIsChatterbox)
            {
                string sampleValue;
                if (samplePath is null)
                {
                    sampleValue = "built-in voice";
                }
                else
                {
                    sampleValue = Path.GetFileName(samplePath) + (sampleExists ? " ✓" : string.Empty);
                }

                RenderRow(
                    helpers,
                    palette,
                    width,
                    rows,
                    selectedIndex,
                    SetupRow.ChatterboxSample,
                    samplePath is null ? "○" : "●",
                    samplePath is null ? palette.SecondaryText.AnsiFg : palette.PromptFg.AnsiFg,
                    "Voice sample",
                    sampleValue,
                    samplePath is null ? palette.SecondaryText.AnsiFg : palette.PromptFg.AnsiFg,
                    "Change",
                    helperText: "~10 s clip; audio-engine reads it from this path",
                    warningText: samplePath is not null && !sampleExists ? "file not found — Enter to fix" : null,
                    isWarning: samplePath is not null && !sampleExists);

                RenderRow(
                    helpers,
                    palette,
                    width,
                    rows,
                    selectedIndex,
                    SetupRow.ChatterboxExaggeration,
                    "●",
                    palette.PromptFg.AnsiFg,
                    "Expressiveness",
                    exaggerationDisplay,
                    palette.PromptFg.AnsiFg,
                    "Change",
                    helperText: "0 = flat · 0.5 = natural · 1 = dramatic (Chatterbox exaggeration)");
            }
            else
            {
                RenderRow(
                    helpers,
                    palette,
                    width,
                    rows,
                    selectedIndex,
                    SetupRow.Voice,
                    "●",
                    palette.PromptFg.AnsiFg,
                    "TTS voice",
                    voice,
                    palette.PromptFg.AnsiFg,
                    "Change");

                RenderRow(
                    helpers,
                    palette,
                    width,
                    rows,
                    selectedIndex,
                    SetupRow.Model,
                    "●",
                    palette.PromptFg.AnsiFg,
                    "TTS model",
                    model,
                    palette.PromptFg.AnsiFg,
                    "Change");

                var instructionsValue = string.IsNullOrWhiteSpace(instructions)
                    ? "(none)"
                    : TruncateMiddle(instructions, Math.Max(20, width - 40));
                RenderRow(
                    helpers,
                    palette,
                    width,
                    rows,
                    selectedIndex,
                    SetupRow.TtsInstructions,
                    string.IsNullOrWhiteSpace(instructions) ? "○" : "●",
                    string.IsNullOrWhiteSpace(instructions) ? palette.SecondaryText.AnsiFg : palette.PromptFg.AnsiFg,
                    "TTS instructions",
                    instructionsValue,
                    palette.PromptFg.AnsiFg,
                    "Change",
                    helperText: "Style hint sent with each request (gpt-4o-mini-tts only)");
            }

            RenderRow(
                helpers,
                palette,
                width,
                rows,
                selectedIndex,
                SetupRow.NarrationTest,
                narrationTestValue.StartsWith("ready", StringComparison.Ordinal) ? "●" : "○",
                narrationTestValue.StartsWith("ready", StringComparison.Ordinal) ? palette.PromptFg.AnsiFg : palette.SecondaryText.AnsiFg,
                "Test narration",
                narrationTestValue,
                narrationTestValue.StartsWith("ready", StringComparison.Ordinal) ? palette.PromptFg.AnsiFg : palette.SecondaryText.AnsiFg,
                "Test",
                helperText: "Speaks one sentence through audio-engine with the active engine and saves narration-test.m4a");

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
                if (probed is not null && DescribeNarratorStatus(probed, launcher.IsStarting(probed.Url)) != paintedNarratorText)
                {
                    narratorStatus = probed;
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

                if (dispatched is SetupRow.AudioEngine or SetupRow.AudioEngineProgram or SetupRow.AudioEngineKey or SetupRow.NarrationEngine
                    or SetupRow.ChatterboxSample or SetupRow.ChatterboxExaggeration
                    or SetupRow.Voice or SetupRow.Model or SetupRow.TtsInstructions)
                {
                    narratorStatusFresh = false;
                }

                // An engine switch swaps the contextual rows out from under the
                // cursor — keep it on the engine row instead of an unrelated one.
                if (dispatched == SetupRow.NarrationEngine)
                {
                    selectedIndex = Array.IndexOf(rows, SetupRow.NarrationEngine);
                }

                continue;
            }

            // workspace-2xej.9: 't' on the Narration engine row tests the ACTIVE
            // engine (parity with Enter on the Local engine row).
            if (command.RawKeyChar == 't' && rows[selectedIndex] is SetupRow.NarrationEngine or SetupRow.NarrationTest)
            {
                await RunNarrationTestAsync(ctx, options, ct).ConfigureAwait(false);
                continue;
            }

            // workspace-cgnt: 'v' on the GCS bucket or GCS service-account
            // row re-runs the four-step verify probe ad-hoc, so users can
            // sanity-check their existing setup at any time.
            if (command.RawKeyChar == 'v'
                && (rows[selectedIndex] == SetupRow.GcsBucket || rows[selectedIndex] == SetupRow.GcsKey)
                && hasGcsKey
                && hasBucket)
            {
                await RunVerifyFromSettingsAsync(ctx, settingsStore.Get(KeyGcsBucketName)!, ct)
                    .ConfigureAwait(false);
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
            case SetupRow.GcsKey:
                await HandleSetKey(ctx, options, ct).ConfigureAwait(false);
                return;
            case SetupRow.GcsBucket:
                await HandleSetBucket(ctx, options, ct).ConfigureAwait(false);
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

            case SetupRow.Voice:
            {
                using var scope = ctx.ScopeFactory.CreateScope();
                var store = scope.ServiceProvider.GetRequiredService<IUserSettingsStore>();
                var current = store.Get(KeyOpenAiTtsVoice)
                              ?? ResolveTtsDefault(scope, c => c.OpenAiVoice, "nova");
                await PodcastSetupHelpers.PromptAndPickVoiceAsync(
                    ctx, options, store, current, ct).ConfigureAwait(false);
                return;
            }

            case SetupRow.Model:
            {
                using var scope = ctx.ScopeFactory.CreateScope();
                var store = scope.ServiceProvider.GetRequiredService<IUserSettingsStore>();
                var current = store.Get(KeyOpenAiTtsModel)
                              ?? ResolveTtsDefault(scope, c => c.OpenAiModel, "tts-1");
                await PodcastSetupHelpers.PromptAndPickModelAsync(
                    ctx, options, store, current, ct).ConfigureAwait(false);
                return;
            }

            case SetupRow.TtsInstructions:
                await HandleSetTtsInstructions(ctx, options, ct).ConfigureAwait(false);
                return;

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

            case SetupRow.NarrationEngine:
                await HandlePickEngineAsync(ctx, options, ct).ConfigureAwait(false);
                return;

            case SetupRow.ChatterboxSample:
                await HandleSetChatterboxSampleAsync(ctx, options, ct).ConfigureAwait(false);
                return;

            case SetupRow.ChatterboxExaggeration:
                await HandleSetChatterboxExaggerationAsync(ctx, options, ct).ConfigureAwait(false);
                return;

            case SetupRow.NarrationTest:
                await RunNarrationTestAsync(ctx, options, ct).ConfigureAwait(false);
                return;

            case SetupRow.BrowserExtension:
                await BrowserExtensionCard.RunAsync(ctx, options, ct).ConfigureAwait(false);
                return;
        }
    }

    /// <summary>
    /// The Setup screen's row list for the active narration engine — credentials
    /// first, then the narration section (audio-engine rows, engine row, the active
    /// engine's rows, Test narration), then the output rows (workspace-2xej.7, c3ec4.3).
    /// </summary>
    internal static SetupRow[] BuildSetupRows(bool engineIsChatterbox)
    {
        var rowList = new List<SetupRow>
        {
            SetupRow.OpenAiKey,
            SetupRow.LayoutModel,
            SetupRow.GcsKey,
            SetupRow.GcsBucket,
            SetupRow.AudioEngine,
            SetupRow.AudioEngineProgram,
            SetupRow.AudioEngineKey,
            SetupRow.NarrationEngine,
        };
        rowList.AddRange(engineIsChatterbox
            ? new[] { SetupRow.ChatterboxSample, SetupRow.ChatterboxExaggeration, SetupRow.NarrationTest }
            : new[] { SetupRow.Voice, SetupRow.Model, SetupRow.TtsInstructions, SetupRow.NarrationTest });
        rowList.AddRange(new[]
        {
            SetupRow.OutputFolder, SetupRow.AutoPurgeHours, SetupRow.CostGateAlwaysShow, SetupRow.BrowserExtension,
        });
        return [.. rowList];
    }

    /// <summary>
    /// Pure readiness lines for the engine picker (workspace-c3ec4.3): each engine is
    /// "ready" only when audio-engine reports it configured. Extracted so tests cover
    /// every branch without driving the render loop.
    /// </summary>
    /// <param name="status">The last audio-engine status probe; null when it failed.</param>
    /// <param name="voice">The OpenAI voice in effect.</param>
    /// <param name="sampleFileName">File name of the Chatterbox voice sample, or null for the built-in voice.</param>
    /// <returns>One readiness line per engine.</returns>
    internal static (string OpenAi, string Chatterbox) BuildEngineReadiness(
        NarratorStatus? status,
        string voice,
        string? sampleFileName)
    {
        if (status is null || !status.Reachable)
        {
            return ("audio-engine not reachable", "audio-engine not reachable");
        }

        var openAi = FindEngine(status, "openai")?.IsConfigured == true
            ? $"ready — audio-engine has an OpenAI key · voice {voice}"
            : "audio-engine has no OpenAI key — add it in audio-engine's settings";

        string chatterbox;
        if (FindEngine(status, "chatterbox")?.IsConfigured != true)
        {
            chatterbox = "audio-engine has no chatterbox engine";
        }
        else if (sampleFileName is not null)
        {
            chatterbox = $"ready · sample {sampleFileName}";
        }
        else
        {
            chatterbox = "ready · built-in voice";
        }

        return (openAi, chatterbox);
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

    /// <summary>
    /// The configured Chatterbox voice-sample path resolved to absolute (relative paths
    /// resolve against the current directory), WITHOUT an existence check. Null when
    /// unset/blank (built-in voice).
    /// </summary>
    /// <param name="store">The user settings store.</param>
    /// <returns>The absolute sample path, or null.</returns>
    internal static string? ResolveChatterboxSamplePath(IUserSettingsStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        var saved = store.Get(KeyChatterboxVoiceSample);
        if (string.IsNullOrWhiteSpace(saved))
        {
            return null;
        }

        return Path.GetFullPath(saved.Trim(), Environment.CurrentDirectory);
    }

    private static NarratorEngine? FindEngine(NarratorStatus status, string name)
        => status.Engines.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Effective Chatterbox exaggeration: the settings key (invariant float) wins over
    /// the bound <see cref="AudioEngineConfiguration"/> default; garbage/absent falls
    /// back; clamped to [0, 2].
    /// </summary>
    private static double ResolveChatterboxExaggeration(IServiceScope scope, IUserSettingsStore store)
    {
        var saved = store.Get(KeyChatterboxExaggeration);
        if (!string.IsNullOrWhiteSpace(saved)
            && double.TryParse(saved, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            && !double.IsNaN(parsed)
            && !double.IsInfinity(parsed))
        {
            return Math.Clamp(parsed, 0d, 2d);
        }

        var opts = scope.ServiceProvider.GetService<IOptions<AudioEngineConfiguration>>();
        return Math.Clamp(opts?.Value.ChatterboxExaggeration ?? 0.5, 0d, 2d);
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

    /// <summary>
    /// Ad-hoc rerun entry point for the GCS four-step verify probe, bound
    /// to <c>v</c> on the GCS bucket / service-account rows
    /// (workspace-cgnt). Renders the verify panel inline and waits for a
    /// keypress before returning so the user can read the result.
    /// </summary>
    private static async Task RunVerifyFromSettingsAsync(
        CommandContext ctx,
        string bucketName,
        CancellationToken ct)
    {
        using var scope = ctx.ScopeFactory.CreateScope();
        var cloudStorage = scope.ServiceProvider.GetRequiredService<ICloudStorageClient>();
        if (cloudStorage is not GcsStorageClient gcsClient)
        {
            return;
        }

        var palette = BuiltInThemes.Get(ctx.ThemeProvider.CurrentTheme);

        // workspace-ur5h: NO Console.Clear, NO wizard header. The verify
        // panel renders inline so the Settings rows stay visible while the
        // four-step probe runs.
        var panelRow = Math.Max(1, (Console.WindowHeight / 2) - 2);

        try
        {
            await PodcastGcsWizard.RunVerifyStepAsync(ctx, gcsClient, palette, bucketName, panelRow, ct)
                .ConfigureAwait(false);

            Console.SetCursorPosition(UI.OverlayViewport.Left + 2, panelRow + 7);
            Console.Write($"{palette.SecondaryText.AnsiFg}Press any key to return{Reset}");
            try
            {
                _ = await ctx.InputHandler.WaitForInputAsync(ct).ConfigureAwait(false);

                // workspace-khpe.7: the dismiss key is consumed above; drain any
                // extra keys mashed while the panel was up so they don't ghost
                // into the Settings screen once it re-renders.
                ctx.InputHandler.DrainBufferedInput();
            }
            catch (OperationCanceledException)
            {
                // ignore
            }
        }
        finally
        {
            // Wipe the inline panel + prompt line on the way out so the
            // Settings screen is restored when the caller re-renders.
            for (var i = 0; i < 9; i++)
            {
                try
                {
                    Console.SetCursorPosition(UI.OverlayViewport.Left, panelRow + i);
                }
                catch (ArgumentOutOfRangeException)
                {
                    break;
                }

                Console.Write(new string(' ', Math.Max(20, UI.OverlayViewport.Width - 1)));
            }
        }
    }

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
        helpers.WriteLine(mainLine);
        if (subLine != null)
        {
            helpers.WriteLine(subLine);
        }
    }

    private static string ResolveDefaultOutputFolder() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WireCopy",
            "output");

    /// <summary>
    /// Returns a status-row label for the configured service-account key —
    /// the masked client_email when we have it cached, the bare filename
    /// otherwise. The cache is written by <see cref="HandleSetKey"/> at save
    /// time so we don't pay a synchronous file read on every Setup render
    /// (workspace-x7lf QA fix). Logs when falling back so silent failures
    /// don't hide.
    /// </summary>
    private static string? ResolveGcsKeyDisplay(IUserSettingsStore store, ILogger? logger = null)
    {
        var keyPath = store.Get(KeyGcsServiceAccountKeyPath);
        if (string.IsNullOrWhiteSpace(keyPath))
        {
            return null;
        }

        var cached = store.Get(KeyGcsServiceAccountDisplay);
        if (!string.IsNullOrWhiteSpace(cached))
        {
            return cached;
        }

        // No cache (e.g. legacy install that pre-dates the cache). Best-effort
        // read of the key file once, then populate the cache so future renders
        // hit the fast path. Failures are logged at debug — we still need to
        // render *something* in the row.
        try
        {
            if (!File.Exists(keyPath))
            {
                logger?.LogDebug("Service account key file not at {Path}; falling back to filename for status row", keyPath);
                return Path.GetFileName(keyPath);
            }

            var json = File.ReadAllText(keyPath);
            var (email, _) = GcsStorageClient.ExtractKeyMetadata(json);
            if (string.IsNullOrWhiteSpace(email))
            {
                return Path.GetFileName(keyPath);
            }

            var masked = GcsStorageClient.MaskServiceAccountEmail(email);
            try
            {
                store.Set(KeyGcsServiceAccountDisplay, masked);
            }
            catch (Exception cacheEx)
            {
                logger?.LogDebug(cacheEx, "Failed to populate service account display cache");
            }

            return masked;
        }
        catch (Exception ex)
        {
            // Don't let display issues hide the row — show the filename.
            logger?.LogDebug(ex, "Couldn't read key file at {Path} for status display", keyPath);
            return Path.GetFileName(keyPath);
        }
    }

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

            case "bucket":
                await HandleSetBucket(ctx, options, ct).ConfigureAwait(false);
                break;

            case "key":
                await HandleSetKey(ctx, options, ct).ConfigureAwait(false);
                break;

            default:
                ctx.NavigationService.SetStatusMessage("Usage: :set apikey | :set bucket | :set key");
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

            case "bucket":
                await HandleClearBucket(ctx, options, ct).ConfigureAwait(false);
                break;

            case "key":
                await HandleClearKey(ctx, options, ct).ConfigureAwait(false);
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
    /// GCS bucket Setup row (workspace-dwgl). Uses the same inline FormField
    /// component as the API-key rows, hard-blocks on missing service account,
    /// and runs a real GCP probe on submit with three terminal states
    /// (Verified / NotFound / AccessDenied).
    ///
    /// <para>
    /// Workspace-dlq5 wired up the long-promised <c>?</c> overlay help via the
    /// new <see cref="FormFieldConfig.OnExtraKey"/> hook — pressing <c>?</c>
    /// while editing the bucket name renders a 12-line guidance panel below
    /// the field; any key dismisses it.
    /// </para>
    /// </summary>
    private static async Task HandleSetBucket(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        // --- Prerequisite gate: service account must be set first ---
        using (var gateScope = ctx.ScopeFactory.CreateScope())
        {
            var gateStore = gateScope.ServiceProvider.GetRequiredService<IUserSettingsStore>();
            if (string.IsNullOrWhiteSpace(gateStore.Get(KeyGcsServiceAccountKeyPath)))
            {
                var gateChoice = await ShowPrerequisiteGateAsync(ctx, ct).ConfigureAwait(false);
                if (gateChoice == 'a')
                {
                    await HandleSetKey(ctx, options, ct).ConfigureAwait(false);
                }

                await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
                return;
            }
        }

        // --- FormField + probe loop (allows "Edit name" to reopen with the previous value) ---
        // workspace-ur5h: NO Console.Clear. Mirrors the OpenAI inline pattern.
        // workspace-spue: ask for the full public URL the feed will live at
        // (https://storage.googleapis.com/{bucket}/feed.xml). The user's
        // mental model is "this is where my feed will be exposed publicly",
        // not "this is a bucket name." We still parse the bucket back out
        // via BucketUrlParser before persisting.
        // workspace-76ig: pre-fill the URL form of the smart default
        // (https://storage.googleapis.com/{project_id}-wirecopy-feed/feed.xml).
        string? prefilledName = ResolveBucketSmartDefault(ctx);
        string? prefilledInput = BucketUrlParser.BuildFeedUrl(prefilledName) ?? prefilledName;

        while (!ct.IsCancellationRequested)
        {
            var palette = BuiltInThemes.Get(ctx.ThemeProvider.CurrentTheme);
            var startRow = Math.Max(1, (Console.WindowHeight / 2) - 3);
            var fieldWidth = Math.Min(Math.Max(40, UI.OverlayViewport.Width) - 6, 60);

            // No subtitle now — label + helptext alone keep the field at
            // FormField.Height (5 rows). The probe panel anchors directly
            // below that.
            var helpRow = startRow + FormField.Height + 1;
            var maxCopy = GcsCopy.MaxCopyWidth(fieldWidth);

            var field = new FormFieldConfig
            {
                Label = GcsCopy.FitOrShorten(
                    "Public feed URL (where your feed will live)",
                    "Public feed URL",
                    maxCopy),
                Placeholder = GcsCopy.FitOrShorten(
                    "https://storage.googleapis.com/your-bucket-name/feed.xml",
                    "https://storage.googleapis.com/<bucket>/feed.xml",
                    Math.Max(10, fieldWidth - 4)),
                HelpText = GcsCopy.FitOrShorten(
                    "? for help · Enter to verify · Esc to cancel",
                    "? help · Enter verify · Esc",
                    maxCopy),
                IsSecret = false,
                MaxLength = 200,
                InitialValue = prefilledInput,
                Validate = ValidateBucketNameInput,
                OnExtraKey = ch =>
                {
                    if (ch != '?')
                    {
                        return false;
                    }

                    // Modal overlay — render, block on a single key, then clear.
                    // The interceptor is invoked synchronously from inside the
                    // input loop; we sync-wait the key channel via GetResult so
                    // the loop doesn't advance until the user dismisses the
                    // overlay. FormField re-renders its own chrome after we
                    // return true, which restores the field above the help
                    // region; we still clear the help-text rows below to wipe
                    // the overlay.
                    GcsHelpOverlays.RenderBucketHelp(palette, helpRow);
                    try
                    {
                        _ = ctx.InputHandler.WaitForInputAsync(ct).GetAwaiter().GetResult();
                    }
                    catch (OperationCanceledException)
                    {
                        // Cancellation — treat as dismiss; the outer loop will
                        // notice ct on its next iteration.
                    }

                    GcsHelpOverlays.ClearOverlay(helpRow, GcsHelpOverlays.BucketOverlayRows);
                    return true;
                },
            };

            var fieldHeight = FormField.HeightFor(field);

            var entered = await FormField.PromptAsync(
                ctx.InputHandler, field, palette, startRow, fieldWidth, ct).ConfigureAwait(false);

            if (entered == null)
            {
                await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
                return;
            }

            // workspace-spue: accept any of the four input shapes
            // (path-style URL, virtual-host URL, gs://, bare). Validate already
            // rejected un-parseable inputs so we can rely on the parser here.
            var name = NormalizeBucketName(entered);

            // --- Probe loop: probe → render state panel → (maybe) loop on Retry ---
            // workspace-ur5h: anchor the probe panel directly under the field
            // (no gap) so the user's eye stays in context while the probe runs.
            var probeRow = startRow + fieldHeight + 1;
            var probeOutcome = await ProbeAndHandleAsync(ctx, name, palette, probeRow, ct).ConfigureAwait(false);

            switch (probeOutcome.Action)
            {
                case ProbeFollowUp.Done:
                    await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
                    return;

                case ProbeFollowUp.EditName:
                    // Re-prompt with the canonical URL form of whatever the
                    // user typed, so a retry looks like an edit rather than a
                    // fresh start. If the previous input was already URL-shaped,
                    // BuildFeedUrl + ParseBucketName gives us back the same URL.
                    prefilledInput = BucketUrlParser.BuildFeedUrl(name) ?? name;
                    continue;

                case ProbeFollowUp.Cancel:
                    await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
                    return;
            }
        }

        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Prerequisite-gate panel for <see cref="HandleSetBucket"/>. Returns the
    /// lowercase character chosen by the user ('a' to set the service account)
    /// or null when they backed out.
    ///
    /// <para>
    /// workspace-ur5h: NO Console.Clear. Renders inline as an overlay on top
    /// of the live Settings rows so the user keeps context — same pattern as
    /// the rest of the Setup screen.
    /// </para>
    /// </summary>
    private static async Task<char?> ShowPrerequisiteGateAsync(CommandContext ctx, CancellationToken ct)
    {
        var palette = BuiltInThemes.Get(ctx.ThemeProvider.CurrentTheme);
        var startRow = Math.Max(1, (Console.WindowHeight / 2) - 2);
        BucketProbePanel.RenderPrerequisiteGate(palette, startRow);
        try
        {
            return await BucketProbePanel.WaitForChoiceAsync(ctx.InputHandler, new[] { 'a' }, ct).ConfigureAwait(false);
        }
        finally
        {
            // Wipe the inline gate so the underlying Settings rows are
            // visible again when we return to the caller. The gate is
            // 5 rows tall (header, blank, body, blank, action), but we
            // wipe a generous 6 to mop up any trailing render.
            for (var i = 0; i < 6; i++)
            {
                try
                {
                    Console.SetCursorPosition(UI.OverlayViewport.Left, startRow + i);
                }
                catch (ArgumentOutOfRangeException)
                {
                    break;
                }

                Console.Write(new string(' ', Math.Max(20, UI.OverlayViewport.Width - 1)));
            }
        }
    }

    /// <summary>
    /// FormField validator for the bucket field. Accepts any of the four
    /// shapes <see cref="BucketUrlParser"/> understands (workspace-spue):
    /// the full storage.googleapis.com path-style or virtual-host URL, the
    /// gs:// prefix, or a bare bucket name (back-compat). Parses to the
    /// bare bucket then defers to <see cref="GcsConfiguration.ExplainBucketInvalid"/>.
    /// </summary>
    internal static string? ValidateBucketNameInput(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "Public feed URL cannot be empty";
        }

        var bucket = BucketUrlParser.ParseBucketName(raw);
        if (string.IsNullOrEmpty(bucket))
        {
            // The user pasted something URL-shaped but we couldn't find a
            // bucket — keep the URL frame in the error so they can spot what
            // they typed vs. what we expected.
            return BucketUrlParser.LooksLikeUrl(raw)
                ? "Couldn't find a bucket name in that URL. Expected https://storage.googleapis.com/<bucket>/feed.xml"
                : "Bucket name cannot be empty";
        }

        return GcsConfiguration.ExplainBucketInvalid(bucket);
    }

    /// <summary>
    /// Returns "{project_id}-wirecopy-feed" parsed from the saved
    /// service-account JSON, or <c>null</c> when no SA key is configured,
    /// the file is missing, or the JSON has no <c>project_id</c> field
    /// (workspace-76ig). The user can accept the default with Enter or
    /// override it by typing.
    /// </summary>
    /// <returns>Pre-fill bucket name candidate, or null when unavailable.</returns>
    internal static string? ResolveBucketSmartDefault(CommandContext ctx)
    {
        try
        {
            using var scope = ctx.ScopeFactory.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IUserSettingsStore>();
            var keyPath = store.Get(KeyGcsServiceAccountKeyPath);
            if (string.IsNullOrWhiteSpace(keyPath) || !File.Exists(keyPath))
            {
                return null;
            }

            var json = File.ReadAllText(keyPath);
            var (_, projectId) = GcsStorageClient.ExtractKeyMetadata(json);
            return BuildSmartDefaultBucket(projectId);
        }
        catch (Exception ex)
        {
            ctx.Logger.LogDebug(ex, "Smart-default bucket lookup failed; using empty pre-fill");
            return null;
        }
    }

    /// <summary>
    /// Builds the smart-default bucket name from a project ID. Returns
    /// <c>null</c> when the project ID is empty or the derived bucket name
    /// would violate the 63-char limit or fail validation. Tested separately
    /// so the file IO path in ResolveBucketSmartDefault stays minimal.
    /// </summary>
    internal static string? BuildSmartDefaultBucket(string? projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return null;
        }

        var candidate = $"{projectId.Trim()}-wirecopy-feed";
        return GcsConfiguration.IsValidBucketName(candidate) ? candidate : null;
    }

    /// <summary>
    /// Returns the bare bucket name parsed from any of the URL forms
    /// described on <see cref="BucketUrlParser"/>, or the trimmed input
    /// itself when the parser couldn't extract anything. Callers downstream
    /// of <see cref="ValidateBucketNameInput"/> rely on the validator to
    /// have rejected un-parseable inputs already; this method just unwraps
    /// the canonical form.
    /// </summary>
    internal static string NormalizeBucketName(string raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }

        return BucketUrlParser.ParseBucketName(raw) ?? raw.Trim();
    }

    private enum ProbeFollowUp
    {
        Done,
        EditName,
        Cancel,
    }

    private readonly record struct ProbeOutcome(ProbeFollowUp Action);

    private static async Task<ProbeOutcome> ProbeAndHandleAsync(
        CommandContext ctx,
        string bucketName,
        ThemePalette palette,
        int panelRow,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            using var scope = ctx.ScopeFactory.CreateScope();
            var cloudStorage = scope.ServiceProvider.GetRequiredService<ICloudStorageClient>();
            var gcsConfig = scope.ServiceProvider.GetRequiredService<IOptions<GcsConfiguration>>().Value;

            if (cloudStorage is not GcsStorageClient gcsClient)
            {
                // Non-GCS backend — fall back to lexical-only persist.
                await PersistBucketAsync(ctx, scope, gcsConfig, bucketName).ConfigureAwait(false);
                return new ProbeOutcome(ProbeFollowUp.Done);
            }

            ClearPanelRegion(panelRow);
            CloudStorageValidationResult result;
            try
            {
                result = await BucketProbePanel.RunWithSpinnerAsync(
                    palette,
                    $"Verifying bucket \"{bucketName}\"…",
                    panelRow,
                    inner => GcsBucketProbe.ProbeAsync(gcsClient, bucketName, inner),
                    ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new ProbeOutcome(ProbeFollowUp.Cancel);
            }

            ClearPanelRegion(panelRow);

            if (result.IsValid)
            {
                var (loc, proj) = await gcsClient.GetBucketInfoAsync(bucketName, ct).ConfigureAwait(false);
                BucketProbePanel.RenderSuccess(
                    palette,
                    panelRow,
                    bucketName,
                    proj ?? gcsConfig.ProjectId ?? "(not configured)",
                    loc ?? "(unknown)");

                await PersistBucketAsync(ctx, scope, gcsConfig, bucketName).ConfigureAwait(false);

                // workspace-cgnt: after the bucket-existence probe says
                // "Verified", run the real four-step verify probe so we
                // exercise upload+download+delete with the configured
                // service account. The probe panel above remains visible
                // for one beat, then we replace it with the verify rows.
                ClearPanelRegion(panelRow);
                var verifyResult = await PodcastGcsWizard.RunVerifyStepAsync(
                    ctx, gcsClient, palette, bucketName, panelRow, ct).ConfigureAwait(false);

                if (!verifyResult.Success)
                {
                    // Surface the failure-class message and let the user
                    // press a key to acknowledge before returning to the
                    // FormField (so they can edit the bucket / fix IAM).
                    await BucketProbePanel.WaitForChoiceAsync(
                        ctx.InputHandler, new[] { '\n', 'r', 'e' }, ct).ConfigureAwait(false);
                    return new ProbeOutcome(ProbeFollowUp.EditName);
                }

                // Per spec — success panel dismisses on Enter or Esc. The helper
                // already short-circuits Enter (ActivateLink) but we surface
                // '\n' explicitly in validKeys to document intent and guard
                // against future helper changes.
                await BucketProbePanel.WaitForChoiceAsync(ctx.InputHandler, new[] { '\n' }, ct).ConfigureAwait(false);
                return new ProbeOutcome(ProbeFollowUp.Done);
            }

            switch (result.ErrorType)
            {
                case CloudStorageValidationErrorType.BucketNotFound:
                {
                    var nextAction = await HandleNotFoundAsync(
                        ctx, scope, gcsClient, gcsConfig, bucketName, palette, panelRow, ct).ConfigureAwait(false);
                    if (nextAction == ProbeFollowUp.Done || nextAction == ProbeFollowUp.Cancel)
                    {
                        return new ProbeOutcome(nextAction);
                    }

                    // EditName → fall back to outer FormField
                    return new ProbeOutcome(ProbeFollowUp.EditName);
                }

                case CloudStorageValidationErrorType.AccessDenied:
                {
                    var serviceAccountEmail = await ResolveServiceAccountEmailAsync(scope).ConfigureAwait(false);
                    BucketProbePanel.RenderAccessDenied(palette, panelRow, bucketName, serviceAccountEmail);

                    var ch = await BucketProbePanel.WaitForChoiceAsync(
                        ctx.InputHandler, new[] { 'r', 'e' }, ct).ConfigureAwait(false);
                    if (ch == 'r')
                    {
                        continue;
                    }

                    return new ProbeOutcome(ch == 'e' ? ProbeFollowUp.EditName : ProbeFollowUp.Cancel);
                }

                default:
                {
                    BucketProbePanel.RenderGenericError(
                        palette,
                        panelRow,
                        result.ErrorMessage ?? "Validation failed",
                        InterpretError(result.ErrorType));

                    var ch = await BucketProbePanel.WaitForChoiceAsync(
                        ctx.InputHandler, new[] { 'r', 'e' }, ct).ConfigureAwait(false);
                    if (ch == 'r')
                    {
                        continue;
                    }

                    return new ProbeOutcome(ch == 'e' ? ProbeFollowUp.EditName : ProbeFollowUp.Cancel);
                }
            }
        }

        return new ProbeOutcome(ProbeFollowUp.Cancel);
    }

    private static async Task<ProbeFollowUp> HandleNotFoundAsync(
        CommandContext ctx,
        IServiceScope scope,
        GcsStorageClient gcsClient,
        GcsConfiguration gcsConfig,
        string bucketName,
        ThemePalette palette,
        int panelRow,
        CancellationToken ct)
    {
        BucketProbePanel.RenderNotFound(
            palette, panelRow, bucketName, gcsConfig.ProjectId ?? "(not configured)");

        var pick = await BucketProbePanel.WaitForChoiceAsync(
            ctx.InputHandler, new[] { 'c', 'e' }, ct).ConfigureAwait(false);
        if (pick == 'e')
        {
            return ProbeFollowUp.EditName;
        }

        if (pick != 'c')
        {
            return ProbeFollowUp.Cancel;
        }

        // Confirm screen
        ClearPanelRegion(panelRow);
        BucketProbePanel.RenderCreateConfirm(
            palette, panelRow, bucketName, gcsConfig.ProjectId ?? "(not configured)", gcsConfig.BucketLocation);

        var confirm = await BucketProbePanel.WaitForChoiceAsync(
            ctx.InputHandler, new[] { 'y', 'n' }, ct).ConfigureAwait(false);
        if (confirm != 'y')
        {
            return ProbeFollowUp.Cancel;
        }

        ClearPanelRegion(panelRow);
        try
        {
            await BucketProbePanel.RunWithSpinnerAsync(
                palette,
                $"Creating bucket \"{bucketName}\"…",
                panelRow,
                async inner =>
                {
                    await GcsBucketProbe.CreateAsync(gcsClient, bucketName, gcsConfig, inner).ConfigureAwait(false);
                    return true;
                },
                ct).ConfigureAwait(false);
        }
        catch (Google.GoogleApiException createEx)
        {
            ClearPanelRegion(panelRow);

            // Per spec — create-failure offers only [E] Edit name · [Esc]
            // Cancel. No [R] retry: the same name will fail identically (e.g.
            // global-uniqueness conflict, IAM denial, quota).
            BucketProbePanel.RenderGenericError(
                palette, panelRow, createEx.Message, InterpretCreateError(createEx), allowRetry: false);

            var followUp = await BucketProbePanel.WaitForChoiceAsync(
                ctx.InputHandler, new[] { 'e' }, ct).ConfigureAwait(false);
            return followUp == 'e' ? ProbeFollowUp.EditName : ProbeFollowUp.Cancel;
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Bucket creation failed for {Bucket}", bucketName);
            ClearPanelRegion(panelRow);

            // Same reasoning as the GoogleApiException branch — no [R] on
            // create-failure; only [E] / [Esc].
            BucketProbePanel.RenderGenericError(palette, panelRow, ex.Message, null, allowRetry: false);
            var followUp = await BucketProbePanel.WaitForChoiceAsync(
                ctx.InputHandler, new[] { 'e' }, ct).ConfigureAwait(false);
            return followUp == 'e' ? ProbeFollowUp.EditName : ProbeFollowUp.Cancel;
        }

        // Created — render success and persist.
        ClearPanelRegion(panelRow);
        var (loc, proj) = await gcsClient.GetBucketInfoAsync(bucketName, ct).ConfigureAwait(false);
        BucketProbePanel.RenderSuccess(
            palette,
            panelRow,
            bucketName,
            proj ?? gcsConfig.ProjectId ?? "(not configured)",
            loc ?? gcsConfig.BucketLocation);
        await PersistBucketAsync(ctx, scope, gcsConfig, bucketName).ConfigureAwait(false);

        // Per spec — success panel dismisses on Enter or Esc. Surface '\n'
        // explicitly so the helper's empty-validKeys path isn't relied upon.
        await BucketProbePanel.WaitForChoiceAsync(ctx.InputHandler, new[] { '\n' }, ct).ConfigureAwait(false);
        return ProbeFollowUp.Done;
    }

    private static Task PersistBucketAsync(
        CommandContext ctx,
        IServiceScope scope,
        GcsConfiguration gcsConfig,
        string bucketName)
    {
        try
        {
            var settingsStore = scope.ServiceProvider.GetRequiredService<IUserSettingsStore>();
            settingsStore.Set(KeyGcsBucketName, bucketName);
            gcsConfig.BucketName = bucketName;
            ctx.NavigationService.SetStatusMessage($"Bucket set to \"{bucketName}\"");
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Failed to persist bucket name");
            ctx.NavigationService.SetStatusMessage("Failed to save bucket name", StatusSeverity.Error);
        }

        return Task.CompletedTask;
    }

    private static async Task<string> ResolveServiceAccountEmailAsync(IServiceScope scope)
    {
        try
        {
            var store = scope.ServiceProvider.GetRequiredService<IUserSettingsStore>();
            var keyPath = store.Get(KeyGcsServiceAccountKeyPath);
            if (!string.IsNullOrWhiteSpace(keyPath) && File.Exists(keyPath))
            {
                var json = await File.ReadAllTextAsync(keyPath).ConfigureAwait(false);
                var (email, _) = GcsStorageClient.ExtractKeyMetadata(json);
                if (!string.IsNullOrWhiteSpace(email))
                {
                    return email;
                }
            }

            return store.Get(KeyGcsServiceAccountDisplay) ?? "(unknown)";
        }
        catch
        {
            // KEEP broad (workspace-3v8z): this feeds a DISPLAY-only "(unknown)" fallback.
            // A prior narrowing attempt to (IOException|UnauthorizedAccessException|
            // InvalidOperationException) let SecurityException escape and broke the
            // bucket-probe access-denied flow. The full thrown set is not known/small, so
            // a catch-all is the correct, behaviour-preserving choice here.
            return "(unknown)";
        }
    }

    private static string? InterpretError(CloudStorageValidationErrorType? errorType) => errorType switch
    {
        CloudStorageValidationErrorType.Timeout => "Network looks slow — check connectivity and retry.",
        CloudStorageValidationErrorType.NetworkError => "Network error — check connectivity and retry.",
        CloudStorageValidationErrorType.CredentialsInvalid => "Service account credentials may be invalid; re-check the key file.",
        CloudStorageValidationErrorType.BucketCreationFailed => "Bucket creation failed — see message above.",
        _ => null,
    };

    private static string? InterpretCreateError(Google.GoogleApiException ex)
    {
        if (ex.HttpStatusCode == System.Net.HttpStatusCode.Conflict)
        {
            return "That name is taken globally — names are unique across all of GCS";
        }

        if (ex.HttpStatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            return "The service account lacks Storage Admin on this project — grant it and retry.";
        }

        if (ex.Message.Contains("quota", StringComparison.OrdinalIgnoreCase))
        {
            return "Project quota exceeded";
        }

        return null;
    }

    private static void ClearPanelRegion(int startRow)
    {
        var height = Math.Max(0, Console.WindowHeight - startRow - 1);
        for (var i = 0; i < height; i++)
        {
            try
            {
                Console.SetCursorPosition(UI.OverlayViewport.Left, startRow + i);
            }
            catch (ArgumentOutOfRangeException)
            {
                break;
            }

            var width = Math.Max(20, UI.OverlayViewport.Width - 1);
            Console.Write(new string(' ', width));
        }

        try
        {
            Console.SetCursorPosition(UI.OverlayViewport.Left, startRow);
        }
        catch (ArgumentOutOfRangeException)
        {
            // ignore
        }
    }

    /// <summary>
    /// GCS service-account key entry. Accepts either:
    /// • Pasted JSON content (the contents of the downloaded key file).
    /// • A path to the JSON file on disk (legacy — supported for users who
    ///   already have a path workflow).
    ///
    /// Detection is by leading character: a `{` means JSON, anything else is
    /// treated as a path. Paths support `~/` expansion. Workspace-x7lf rewrote
    /// this to fix the bug where pasted JSON triggered "File not found" — the
    /// old validator only ran File.Exists.
    /// </summary>
    private static async Task HandleSetKey(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        var palette = BuiltInThemes.Get(ctx.ThemeProvider.CurrentTheme);

        // workspace-ur5h: NO Console.Clear, NO wizard header. Mirror the
        // OpenAI inline pattern so the FormField lands as an inline overlay
        // on the live Settings screen — exactly like every other Setup row.
        // The previous wizard chrome (workspace-cgnt) introduced the 9-line
        // void between the header and the input that the user reported
        // was "the wrong place for input."
        var startRow = Math.Max(1, (Console.WindowHeight / 2) - 3);
        var fieldWidth = Math.Min(Math.Max(40, UI.OverlayViewport.Width) - 6, 60);
        var helpRow = startRow + FormField.Height + 1;

        // Width-aware copy. Every visible string is wrapped at
        // GcsCopy.MaxCopyWidth(fieldWidth) so nothing truncates at width 80.
        var maxCopy = GcsCopy.MaxCopyWidth(fieldWidth);

        FormFieldConfig field = null!;
        field = new FormFieldConfig
        {
            Label = GcsCopy.FitOrShorten(
                "Paste your GCP service account JSON, or a file path",
                "Paste service account JSON or path",
                maxCopy),
            Placeholder = GcsCopy.FitOrShorten(
                "Paste JSON content here, or type ~/keys/sa.json",
                "Paste JSON or path",
                Math.Max(10, fieldWidth - 4)),
            HelpText = GcsCopy.FitOrShorten(
                "Press ? for a beginner guide · Esc to cancel",
                "? for help · Esc to cancel",
                maxCopy),
            MaxLength = 8192, // service-account JSON keys are ~2-3 KB; allow headroom.
            Validate = v =>
            {
                var sanitized = SanitizeKeyInput(v);
                if (string.IsNullOrEmpty(sanitized))
                {
                    return "Nothing pasted. Copy the JSON file's contents and try again.";
                }

                // JSON content — validate locally without touching disk.
                // Detection: leading `{` is the obvious case. Non-`{` input
                // that contains whitespace can't be a path (paths don't have
                // embedded newlines / spaces in any sensible workflow), so
                // route those through the JSON validator too — that gives
                // the user a "doesn't look like JSON" error instead of a
                // confusing "File not found at /workspace/not valid json".
                if (sanitized.StartsWith('{') || ContainsWhitespace(sanitized))
                {
                    var jsonResult = GcsStorageClient.ValidateKeyContent(sanitized);
                    return jsonResult.IsValid ? null : jsonResult.ErrorMessage;
                }

                // Otherwise treat as a path. Expand ~/ first.
                var path = sanitized;
                if (path.StartsWith('~'))
                {
                    var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    path = Path.Combine(home, path[1..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                }

                try
                {
                    path = Path.GetFullPath(path);
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    return "That looks like neither JSON nor a usable file path. Paste the JSON contents (starting with {) or a path to the file.";
                }

                var fileResult = GcsStorageClient.ValidateKeyFile(path);
                return fileResult.IsValid ? null : fileResult.ErrorMessage;
            },
            OnExtraKey = ch =>
            {
                if (ch != '?')
                {
                    return false;
                }

                // Beginner on-ramp: explains what GCS is, what a service
                // account key is, where to get one. Modal — any key dismisses.
                GcsHelpOverlays.RenderServiceAccountHelp(palette, helpRow);
                try
                {
                    _ = ctx.InputHandler.WaitForInputAsync(ct).GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                    // dismiss
                }

                GcsHelpOverlays.ClearOverlay(helpRow, GcsHelpOverlays.ServiceAccountOverlayRows);
                return true;
            },
        };

        var input = await FormField.PromptAsync(ctx.InputHandler, field, palette, startRow, fieldWidth, ct).ConfigureAwait(false);

        if (input == null)
        {
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
            return;
        }

        var sanitizedInput = SanitizeKeyInput(input);
        if (string.IsNullOrEmpty(sanitizedInput))
        {
            // Validator should have caught this, but guard against an
            // out-of-band empty submission.
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
            return;
        }

        try
        {
            using var scope = ctx.ScopeFactory.CreateScope();
            var cloudStorage = scope.ServiceProvider.GetRequiredService<ICloudStorageClient>();

            if (cloudStorage is not GcsStorageClient gcsClient)
            {
                // Non-GCS storage backend — fall back to raw settings persist
                // and let the runtime code path surface any errors.
                var settingsStore = scope.ServiceProvider.GetRequiredService<IUserSettingsStore>();
                settingsStore.Set(KeyGcsServiceAccountKeyPath, sanitizedInput, encrypt: true);
                ctx.NavigationService.SetStatusMessage("Service account saved");
                await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
                return;
            }

            ServiceAccountKeyValidationResult result;
            string? maskedEmail = null;
            string? extractedJson = null;

            // Mirror the validator's detection — see Validate above.
            if (sanitizedInput.StartsWith('{') || ContainsWhitespace(sanitizedInput))
            {
                result = gcsClient.SetServiceAccountKeyContent(sanitizedInput);
                if (result.IsValid)
                {
                    extractedJson = sanitizedInput;
                }
            }
            else
            {
                var path = sanitizedInput;
                if (path.StartsWith('~'))
                {
                    var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    path = Path.Combine(home, path[1..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                }

                path = Path.GetFullPath(path);
                result = gcsClient.SetServiceAccountKeyPath(path);
                if (result.IsValid)
                {
                    try
                    {
                        extractedJson = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        ctx.Logger.LogDebug(ex, "Couldn't read key file for status display");
                    }
                }
            }

            if (result.IsValid)
            {
                if (!string.IsNullOrEmpty(extractedJson))
                {
                    var (email, _) = GcsStorageClient.ExtractKeyMetadata(extractedJson);
                    if (!string.IsNullOrWhiteSpace(email))
                    {
                        maskedEmail = GcsStorageClient.MaskServiceAccountEmail(email);
                    }
                }

                // Cache the masked label for the Setup row so we don't have to
                // re-read the key file on every Setup render (workspace-x7lf
                // QA fix #6).
                var settingsStore = scope.ServiceProvider.GetRequiredService<IUserSettingsStore>();
                if (string.IsNullOrEmpty(maskedEmail))
                {
                    settingsStore.Remove(KeyGcsServiceAccountDisplay);
                }
                else
                {
                    settingsStore.Set(KeyGcsServiceAccountDisplay, maskedEmail);
                }

                // workspace-p1me: run an auth-only probe against GCS so a typo
                // / wrong-project / revoked key surfaces here instead of later
                // during bucket setup or podcast generation.
                ctx.NavigationService.SetStatusMessage(
                    string.IsNullOrEmpty(maskedEmail)
                        ? "Verifying credentials with Google Cloud…"
                        : $"Verifying {maskedEmail} with Google Cloud…");
                await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);

                var authOps = new GcsVerifyOps(gcsClient);
                var (authFailure, authMessage) = await authOps.AuthenticateAsync(ct).ConfigureAwait(false);

                if (authFailure is GcsVerifyFailureClass cls && cls != GcsVerifyFailureClass.None)
                {
                    var remediation = GcsCredentialVerifier.RemediationFor(cls);
                    ctx.Logger.LogWarning(
                        "Service account saved but GCP auth probe failed: {Class} — {Message}",
                        cls,
                        authMessage);
                    ctx.NavigationService.SetStatusMessage(
                        $"Saved, but auth failed: {remediation}");
                }
                else
                {
                    ctx.NavigationService.SetStatusMessage(
                        string.IsNullOrEmpty(maskedEmail)
                            ? "Service account saved · authenticated with Google Cloud"
                            : $"Service account saved · authenticated as {maskedEmail}");
                }
            }
            else
            {
                ctx.NavigationService.SetStatusMessage(result.ErrorMessage ?? "Failed to save service account key", StatusSeverity.Error);
            }
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Failed to persist service account key");
            ctx.NavigationService.SetStatusMessage("Failed to save service account key", StatusSeverity.Error);
        }

        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// True when the input contains an internal whitespace character (space,
    /// tab, newline). Used to disambiguate paste-of-not-JSON from a file
    /// path: real paths don't contain whitespace.
    /// </summary>
    private static bool ContainsWhitespace(string s) => s.Any(char.IsWhiteSpace);

    /// <summary>
    /// Strips bracketed-paste markers (`\x1b[200~`/`\x1b[201~`) and trims
    /// surrounding whitespace from a key entry. Mirrors the wizard's
    /// <c>SanitizeKeyInput</c> behaviour so both entry points handle terminal
    /// quirks identically.
    /// </summary>
    private static string SanitizeKeyInput(string raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }

        // Defensive — TerminalInputHandler usually consumes these, but if a
        // paste lands here untrimmed (e.g. from the command line), strip them.
        var s = raw;
        if (s.Contains("\x1b[200~", StringComparison.Ordinal))
        {
            s = s.Replace("\x1b[200~", string.Empty, StringComparison.Ordinal);
        }

        if (s.Contains("\x1b[201~", StringComparison.Ordinal))
        {
            s = s.Replace("\x1b[201~", string.Empty, StringComparison.Ordinal);
        }

        return s.Trim();
    }

    /// <summary>
    /// Prompts for the TTS instruction string and persists it to the settings
    /// store under <see cref="KeyOpenAiTtsInstructions"/>. Empty input keeps
    /// the existing value (use "reset" / "clear" to fall back to the bound
    /// configuration default; the literal "none" persists an empty override
    /// so no <c>instructions</c> field is sent on requests).
    /// </summary>
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

    private static async Task HandleSetTtsInstructions(
        CommandContext ctx,
        RenderOptions options,
        CancellationToken ct)
    {
        using var scope = ctx.ScopeFactory.CreateScope();
        var settingsStore = scope.ServiceProvider.GetRequiredService<IUserSettingsStore>();
        var current = settingsStore.Get(KeyOpenAiTtsInstructions)
                      ?? ResolveTtsDefault(scope, c => c.OpenAiInstructions ?? string.Empty, string.Empty);

        var prompt = string.IsNullOrEmpty(current)
            ? "TTS instructions (Enter blank to keep, 'reset' to revert): "
            : $"TTS instructions [{TruncateMiddle(current, 40)}] (blank=keep, 'reset'=revert, 'none'=disable): ";

        var input = await ctx.InputHandler.PromptForInputAsync(
            prompt, ct, isSecret: false, initialInput: current).ConfigureAwait(false);

        if (input == null || string.IsNullOrWhiteSpace(input))
        {
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
            return;
        }

        var trimmed = input.Trim();

        if (trimmed.Equals("reset", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                settingsStore.Remove(KeyOpenAiTtsInstructions);
                ctx.NavigationService.SetStatusMessage("TTS instructions reset to default");
            }
            catch (Exception ex)
            {
                ctx.Logger.LogWarning(ex, "Failed to clear TTS instructions override");
                ctx.NavigationService.SetStatusMessage("Failed to reset TTS instructions", StatusSeverity.Error);
            }

            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
            return;
        }

        // "none" persists an empty string so the request omits instructions —
        // distinct from "reset" which falls back to the bound default.
        var toPersist = trimmed.Equals("none", StringComparison.OrdinalIgnoreCase) ? string.Empty : trimmed;

        try
        {
            settingsStore.Set(KeyOpenAiTtsInstructions, toPersist);
            ctx.NavigationService.SetStatusMessage(
                string.IsNullOrEmpty(toPersist)
                    ? "TTS instructions disabled"
                    : "TTS instructions saved");
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Failed to persist TTS instructions");
            ctx.NavigationService.SetStatusMessage("Failed to save TTS instructions", StatusSeverity.Error);
        }

        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
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

    /// <summary>
    /// Enter on the Audio engine program row (workspace-08e4u): prompts for the folder or executable
    /// WireCopy starts when nothing answers at the audio-engine URL. Blank forgets the setting so the
    /// <c>AUDIO_ENGINE_HOME</c> / <c>~/audio-engine</c> lookup applies again; either way the launcher's
    /// one-attempt guard is reset so the next probe tries the new program.
    /// </summary>
    private static async Task HandleSetAudioEngineProgramAsync(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        using var scope = ctx.ScopeFactory.CreateScope();
        var settingsStore = scope.ServiceProvider.GetRequiredService<IUserSettingsStore>();
        var launcher = scope.ServiceProvider.GetRequiredService<IAudioEngineLauncher>();
        var palette = BuiltInThemes.Get(ctx.ThemeProvider.CurrentTheme);
        var field = new FormFieldConfig
        {
            Label = "Audio engine program",
            Placeholder = "~/audio-engine",
            HelpText = "Folder or executable WireCopy starts for you · blank to forget · Enter to save · Esc to cancel",
            IsSecret = false,
            MaxLength = 400,
            InitialValue = settingsStore.Get(KeyAudioEngineProgram) ?? string.Empty,
            Validate = ValidateAudioEngineProgram,
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
            if (trimmed.Length == 0)
            {
                settingsStore.Remove(KeyAudioEngineProgram);
                ctx.NavigationService.SetStatusMessage("Audio engine program → found automatically");
            }
            else
            {
                settingsStore.Set(KeyAudioEngineProgram, trimmed);
                ctx.NavigationService.SetStatusMessage($"Audio engine program → {trimmed}");
            }

            launcher.ResetAttempts();
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Failed to save audio-engine program");
            ctx.NavigationService.SetStatusMessage("Failed to save audio-engine program", StatusSeverity.Error);
        }

        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Validates the Audio engine program field (workspace-08e4u): blank is allowed (forgets the
    /// setting); otherwise the path, with <c>~</c> expanded, must be an existing file or folder.
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
            full = AudioEngineLauncher.ExpandHome(value.Trim(), home);
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

    /// <summary>
    /// Enter on the Narration engine row (workspace-2xej.7, c3ec4.3): shows the two-engine
    /// picker with readiness lines taken from audio-engine's status, persists the choice,
    /// and re-renders. audio-engine owns the engines' keys, so nothing chains afterwards.
    /// </summary>
    private static async Task HandlePickEngineAsync(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        using var scope = ctx.ScopeFactory.CreateScope();
        var settingsStore = scope.ServiceProvider.GetRequiredService<IUserSettingsStore>();

        var currentIsChatterbox = string.Equals(
            settingsStore.Get(KeyTtsEngine), "chatterbox", StringComparison.OrdinalIgnoreCase);
        var voice = settingsStore.Get(KeyOpenAiTtsVoice) ?? ResolveTtsDefault(scope, c => c.OpenAiVoice, "nova");
        var samplePath = ResolveChatterboxSamplePath(settingsStore);
        var status = await ProbeNarratorAsync(scope, ct).ConfigureAwait(false);
        var (openAiReadiness, chatterboxReadiness) = BuildEngineReadiness(
            status,
            voice,
            samplePath is not null ? Path.GetFileName(samplePath) : null);

        _ = await PodcastSetupHelpers.PromptAndPickEngineAsync(
            ctx, options, settingsStore, currentIsChatterbox, openAiReadiness, chatterboxReadiness, ct)
            .ConfigureAwait(false);

        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Enter on the Voice sample row (workspace-2xej.8): prompts for a clip path
    /// with ~ expansion + voices/ resolution, validates extension/size, probes the
    /// duration as an advisory, and persists the RESOLVED absolute path. 'none'
    /// reverts to the built-in voice. The file itself is never copied or moved.
    /// </summary>
    private static async Task HandleSetChatterboxSampleAsync(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        using var scope = ctx.ScopeFactory.CreateScope();
        var settingsStore = scope.ServiceProvider.GetRequiredService<IUserSettingsStore>();
        var current = settingsStore.Get(KeyChatterboxVoiceSample);

        var prompt = string.IsNullOrWhiteSpace(current)
            ? "Voice sample path — ~10s wav/mp3 of the voice to clone (drop it in voices/, Enter blank to keep built-in): "
            : $"Voice sample [{Path.GetFileName(current)}] (blank=keep, 'none'=built-in voice, path=replace): ";

        var input = await ctx.InputHandler.PromptForInputAsync(prompt, ct).ConfigureAwait(false);

        if (input is null || string.IsNullOrWhiteSpace(input))
        {
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
            return;
        }

        var trimmed = input.Trim();
        if (trimmed.Equals("none", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("clear", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("builtin", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                settingsStore.Remove(KeyChatterboxVoiceSample);
                ctx.NavigationService.SetStatusMessage("Voice sample → built-in voice");
            }
            catch (Exception ex)
            {
                ctx.Logger.LogWarning(ex, "Failed to clear voice sample");
                ctx.NavigationService.SetStatusMessage("Failed to clear voice sample", StatusSeverity.Error);
            }

            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
            return;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var resolved = ResolveSampleInput(trimmed, Environment.CurrentDirectory, home, File.Exists);
        if (resolved is null)
        {
            ctx.NavigationService.SetStatusMessage(
                $"Not found: {trimmed} — looked in ., voices/",
                StatusSeverity.Error);
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
            return;
        }

        var validationError = ValidateSampleFile(resolved, new FileInfo(resolved).Length);
        if (validationError is not null)
        {
            ctx.NavigationService.SetStatusMessage(validationError, StatusSeverity.Error);
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
            return;
        }

        try
        {
            settingsStore.Set(KeyChatterboxVoiceSample, resolved);

            // Duration advisory (never blocks): a ~10 s clean clip clones best.
            var durationNote = await ProbeSampleDurationNoteAsync(resolved, ctx.Logger).ConfigureAwait(false);
            ctx.NavigationService.SetStatusMessage(
                durationNote ?? $"Voice sample → {Path.GetFileName(resolved)}");
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Failed to persist voice sample path");
            ctx.NavigationService.SetStatusMessage("Failed to save voice sample", StatusSeverity.Error);
        }

        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Enter on the Expressiveness row (workspace-2xej.8): 0.0–2.0 float with
    /// invariant parsing (',' normalized to '.'), 'reset' reverts to the config
    /// default, very high values get a pacing warning appended.
    /// </summary>
    private static async Task HandleSetChatterboxExaggerationAsync(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        var input = await ctx.InputHandler.PromptForInputAsync(
            "Expressiveness 0.0–2.0 (0=flat, 0.5=natural, 1=dramatic; 'reset' for default): ",
            ct).ConfigureAwait(false);

        if (input is null || string.IsNullOrWhiteSpace(input))
        {
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
            return;
        }

        var trimmed = input.Trim();
        using var scope = ctx.ScopeFactory.CreateScope();
        var settingsStore = scope.ServiceProvider.GetRequiredService<IUserSettingsStore>();

        if (trimmed.Equals("reset", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                settingsStore.Remove(KeyChatterboxExaggeration);
                ctx.NavigationService.SetStatusMessage("Expressiveness reset to default");
            }
            catch (Exception ex)
            {
                ctx.Logger.LogWarning(ex, "Failed to clear expressiveness override");
                ctx.NavigationService.SetStatusMessage("Failed to reset expressiveness", StatusSeverity.Error);
            }

            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
            return;
        }

        var (ok, value, error) = ParseExaggeration(trimmed);
        if (!ok)
        {
            ctx.NavigationService.SetStatusMessage(error!, StatusSeverity.Error);
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
            return;
        }

        try
        {
            settingsStore.Set(
                KeyChatterboxExaggeration,
                value.ToString("0.0###", System.Globalization.CultureInfo.InvariantCulture));

            // Chatterbox docs: high exaggeration speeds speech up noticeably.
            var suffix = value > 1.2f ? " — very high values can rush the pacing" : string.Empty;
            ctx.NavigationService.SetStatusMessage(
                $"Expressiveness → {value.ToString("0.0###", System.Globalization.CultureInfo.InvariantCulture)}{suffix}");
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Failed to persist expressiveness");
            ctx.NavigationService.SetStatusMessage("Failed to save expressiveness", StatusSeverity.Error);
        }

        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves the user's sample input to an existing absolute path: ~ expands to
    /// home; relative inputs try the cwd, then cwd/voices/ (so typing just
    /// "mine.wav" finds voices/mine.wav). Null when nothing exists.
    /// </summary>
    internal static string? ResolveSampleInput(string input, string cwd, string home, Func<string, bool> fileExists)
    {
        var candidate = input.Trim();
        if (candidate.StartsWith('~'))
        {
            candidate = Path.Combine(home, candidate.TrimStart('~', '/', '\\'));
        }

        if (Path.IsPathRooted(candidate))
        {
            return fileExists(candidate) ? Path.GetFullPath(candidate) : null;
        }

        var inCwd = Path.GetFullPath(Path.Combine(cwd, candidate));
        if (fileExists(inCwd))
        {
            return inCwd;
        }

        var inVoices = Path.GetFullPath(Path.Combine(cwd, "voices", candidate));
        return fileExists(inVoices) ? inVoices : null;
    }

    /// <summary>Null when acceptable; otherwise the status-line error text.</summary>
    internal static string? ValidateSampleFile(string path, long sizeBytes)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not (".wav" or ".mp3" or ".flac" or ".m4a" or ".ogg"))
        {
            return $"Unsupported extension '{ext}' — use wav, mp3, flac, m4a, or ogg";
        }

        const long maxBytes = 50L * 1024 * 1024;
        if (sizeBytes > maxBytes)
        {
            return $"That's not a short clip ({sizeBytes / (1024 * 1024)} MB) — use a ~10s sample under 50 MB";
        }

        return null;
    }

    /// <summary>Parses the expressiveness input: invariant float, ','→'.', range [0,2].</summary>
    internal static (bool Ok, float Value, string? Error) ParseExaggeration(string input)
    {
        var normalized = input.Trim().Replace(',', '.');
        if (!float.TryParse(
                normalized,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var value)
            || float.IsNaN(value)
            || float.IsInfinity(value)
            || value < 0f
            || value > 2f)
        {
            return (false, 0f, "Enter a number between 0.0 and 2.0");
        }

        return (true, value, null);
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
    /// Advisory duration probe for a just-saved sample. Returns a warning status
    /// line when the clip is shorter than 3 s or longer than 30 s, null when the
    /// duration is fine or ffprobe is unavailable (silently skipped).
    /// </summary>
    private static async Task<string?> ProbeSampleDurationNoteAsync(string path, ILogger logger)
    {
        try
        {
            FfmpegLocator.Configure();
            var info = await FFMpegCore.FFProbe.AnalyseAsync(path).ConfigureAwait(false);
            var seconds = info.Duration.TotalSeconds;
            if (seconds is > 0 and (< 3 or > 30))
            {
                return $"Saved — {seconds:0}s clip; ~10s of clean speech clones best";
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "ffprobe unavailable/failed for sample duration advisory — skipping");
        }

        return null;
    }

    /// <summary>
    /// "Test narration" (workspace-2xej.9, audio-engine since workspace-c3ec4.3):
    /// generates a fixed sentence via the ACTIVE engine through audio-engine in an
    /// inline panel (the first local clip can take a while while the model loads),
    /// saves narration-test.m4a to the output folder, opens it in the OS player, and
    /// stamps ChatterboxLastTestOk on local success. Esc cancels cleanly.
    /// </summary>
    private static async Task RunNarrationTestAsync(
        CommandContext ctx,
        RenderOptions options,
        CancellationToken ct,
        Action<string>? openFile = null)
    {
        using var scope = ctx.ScopeFactory.CreateScope();
        var settingsStore = scope.ServiceProvider.GetRequiredService<IUserSettingsStore>();
        var narrator = scope.ServiceProvider.GetRequiredService<IPodcastNarrator>();
        var engineIsChatterbox = string.Equals(
            settingsStore.Get(KeyTtsEngine), "chatterbox", StringComparison.OrdinalIgnoreCase);
        var outputFolder = settingsStore.Get(KeyPodcastOutputFolder) ?? ResolveDefaultOutputFolder();
        var palette = BuiltInThemes.Get(ctx.ThemeProvider.CurrentTheme);

        // Inline panel — no Console.Clear, Settings rows stay visible around it.
        var panelRow = Math.Max(1, (options.TerminalHeight / 2) - 3);
        var panelWidth = Math.Max(40, Math.Min(options.TerminalWidth - 4, 100));

        // workspace-c3ec4.6: every panel row is blanked edge to edge — the Settings rows
        // underneath are wider than the panel text, so a narrower clear left their tails
        // (and the untouched blank rows) showing through the panel.
        void ClearPanelRow(int row)
        {
            try
            {
                Console.SetCursorPosition(UI.OverlayViewport.Left, panelRow + row);
                Console.Write(new string(' ', Math.Max(20, UI.OverlayViewport.Width - 1)));
            }
            catch (ArgumentOutOfRangeException)
            {
                // Terminal shrank mid-test — skip the paint, never crash the panel.
            }
        }

        void WritePanelLine(int row, string text)
        {
            ClearPanelRow(row);
            try
            {
                Console.SetCursorPosition(UI.OverlayViewport.Left + 2, panelRow + row);
                Console.Write(text);
            }
            catch (ArgumentOutOfRangeException)
            {
                // Terminal shrank mid-test — skip the paint, never crash the panel.
            }
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            for (var i = 0; i < NarrationTestPanelRows; i++)
            {
                ClearPanelRow(i);
            }

            var engineLabel = engineIsChatterbox ? "Chatterbox (local)" : "OpenAI (cloud)";
            var expectation = engineIsChatterbox
                ? "audio-engine runs Chatterbox locally — the first clip can take a while"
                : "Costs well under a cent.";
            WritePanelLine(0, $"{palette.GetAccentFg().AnsiFg}Test narration — {engineLabel}{Reset}");
            WritePanelLine(1, $"{palette.SecondaryText.AnsiFg}{RenderHelpers.TruncateText(expectation, panelWidth - 4)}{Reset}");
            WritePanelLine(2, $"{palette.SecondaryText.AnsiFg}Esc cancels.{Reset}");
            WritePanelLine(4, $"{palette.PrimaryText.AnsiFg}Generating…{Reset}");

            var genTask = ExecuteNarrationTestAsync(narrator, outputFolder, cts.Token);

            // Race generation against Esc; a pending input wait doubles as the
            // final "press any key" consumer so no keystroke is ever swallowed.
            Task<NavigationCommand>? pendingInput = null;
            while (!genTask.IsCompleted)
            {
                pendingInput ??= ctx.InputHandler.WaitForInputAsync(cts.Token);
                var finished = await Task.WhenAny(genTask, pendingInput).ConfigureAwait(false);
                if (finished == pendingInput)
                {
                    NavigationCommand cmd;
                    try
                    {
                        cmd = await pendingInput.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    pendingInput = null;
                    if (cmd.Type is CommandType.GoBack or CommandType.Quit)
                    {
                        await cts.CancelAsync().ConfigureAwait(false);
                        break;
                    }
                }
            }

            NarrationTestOutcome outcome;
            try
            {
                outcome = await genTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                outcome = new NarrationTestOutcome(false, null, "cancelled");
            }

            if (outcome.Success && outcome.FilePath is not null)
            {
                settingsStore.Set(KeyChatterboxLastTestOk, DateTime.UtcNow.ToString("O"));

                var secondsNote = string.Empty;
                try
                {
                    FfmpegLocator.Configure();
                    var probed = await FFMpegCore.FFProbe.AnalyseAsync(outcome.FilePath).ConfigureAwait(false);
                    secondsNote = $" ({probed.Duration.TotalSeconds:0.#}s of audio)";
                }
                catch (Exception ex)
                {
                    ctx.Logger.LogDebug(ex, "ffprobe unavailable for narration-test duration");
                }

                var opened = TryOpenFile(outcome.FilePath, openFile, ctx.Logger);
                WritePanelLine(4, $"{palette.PromptFg.AnsiFg}✓ Saved{(opened ? " and opened" : string.Empty)} {NarrationTestFileName}{secondsNote} — press any key{Reset}");
            }
            else if (outcome.Error == "cancelled")
            {
                WritePanelLine(4, $"{palette.SecondaryText.AnsiFg}Cancelled — press any key{Reset}");
            }
            else
            {
                WritePanelLine(4, $"{palette.ErrorFg.AnsiFg}{RenderHelpers.TruncateText(outcome.Error ?? "Narration test failed", panelWidth - 4)}{Reset}");
                WritePanelLine(5, $"{palette.SecondaryText.AnsiFg}Press any key{Reset}");
            }

            try
            {
                if (pendingInput is not null)
                {
                    _ = await pendingInput.ConfigureAwait(false);
                }
                else
                {
                    _ = await ctx.InputHandler.WaitForInputAsync(ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Cancellation raced the dismiss key — fine, we're leaving anyway.
            }

            ctx.InputHandler.DrainBufferedInput();
        }
        finally
        {
            for (var i = 0; i < NarrationTestPanelRows; i++)
            {
                ClearPanelRow(i);
            }
        }
    }

    /// <summary>Fixed sentence every narration test speaks.</summary>
    internal const string NarrationTestSentence = "This is your Wire Copy narrator. Here's how tonight's stories will sound.";

    /// <summary>Filename the narration test writes into the output folder (overwritten each run).</summary>
    internal const string NarrationTestFileName = "narration-test.m4a";

    /// <summary>
    /// The testable core of the narration test: ask audio-engine for the fixed sentence
    /// and have it written into the output folder. No keyboard, no panel.
    /// </summary>
    /// <param name="narrator">The narrator (audio-engine client).</param>
    /// <param name="outputFolder">Folder the clip lands in; created when missing.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The outcome with the clip path on success.</returns>
    internal static async Task<NarrationTestOutcome> ExecuteNarrationTestAsync(
        IPodcastNarrator narrator,
        string outputFolder,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(narrator);
        ArgumentNullException.ThrowIfNull(outputFolder);

        Directory.CreateDirectory(outputFolder);
        var path = Path.Combine(outputFolder, NarrationTestFileName);
        var result = await narrator.NarrateTestAsync(NarrationTestSentence, path, ct).ConfigureAwait(false);
        if (!result.Success)
        {
            return new NarrationTestOutcome(false, null, result.ErrorMessage ?? "audio-engine: narration test failed");
        }

        return new NarrationTestOutcome(true, result.OutputPath ?? path, null);
    }

    private static bool TryOpenFile(string path, Action<string>? openFile, ILogger logger)
    {
        try
        {
            if (openFile is not null)
            {
                openFile(path);
            }
            else
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            }

            return true;
        }
        catch (Exception ex)
        {
            logger.LogInformation(ex, "No OS player available for {Path} — saved without opening", path);
            return false;
        }
    }

    /// <summary>Result of one narration test run (workspace-2xej.9).</summary>
    internal sealed record NarrationTestOutcome(bool Success, string? FilePath, string? Error);

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

    private static async Task HandleClearBucket(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        try
        {
            using var scope = ctx.ScopeFactory.CreateScope();
            var settingsStore = scope.ServiceProvider.GetRequiredService<IUserSettingsStore>();
            settingsStore.Remove(KeyGcsBucketName);

            var gcsConfig = scope.ServiceProvider.GetRequiredService<IOptions<GcsConfiguration>>().Value;
            gcsConfig.BucketName = null;

            ctx.NavigationService.SetStatusMessage("Bucket name cleared — run :set bucket to restore");
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Failed to remove bucket name");
            ctx.NavigationService.SetStatusMessage("Failed to clear bucket name", StatusSeverity.Error);
        }

        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
    }

    private static async Task HandleClearKey(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        try
        {
            using var scope = ctx.ScopeFactory.CreateScope();
            var cloudStorage = scope.ServiceProvider.GetRequiredService<ICloudStorageClient>();

            if (cloudStorage is GcsStorageClient gcsClient)
            {
                gcsClient.ClearServiceAccountKey();
            }
            else
            {
                var settingsStore = scope.ServiceProvider.GetRequiredService<IUserSettingsStore>();
                settingsStore.Remove(KeyGcsServiceAccountKeyPath);
            }

            ctx.NavigationService.SetStatusMessage("Service account key cleared — run :set key to restore");
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Failed to remove service account key");
            ctx.NavigationService.SetStatusMessage("Failed to clear service account key", StatusSeverity.Error);
        }

        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
    }
#pragma warning restore SA1201, SA1202
}
