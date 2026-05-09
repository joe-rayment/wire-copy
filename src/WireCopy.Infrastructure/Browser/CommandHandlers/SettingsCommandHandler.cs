// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
/// Both this screen and <see cref="PodcastConfirmationScreens"/> share the row
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

    private const string Reset = "\x1b[0m";

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
        var rows = new[]
        {
            SetupRow.OpenAiKey,
            SetupRow.GcsKey,
            SetupRow.GcsBucket,
            SetupRow.OutputFolder,
            SetupRow.Voice,
            SetupRow.Model,
            SetupRow.TtsInstructions,
            SetupRow.AutoPurgeHours,
        };
        var selectedIndex = 0;

        while (!ct.IsCancellationRequested)
        {
            using var scope = ctx.ScopeFactory.CreateScope();
            var settingsStore = scope.ServiceProvider.GetRequiredService<IUserSettingsStore>();
            var palette = BuiltInThemes.Get(ctx.ThemeProvider.CurrentTheme);
            var helpers = new RenderHelpers { TerminalHeight = options.TerminalHeight };
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
            var voice = settingsStore.Get(KeyOpenAiTtsVoice) ?? ResolveTtsDefault(scope, c => c.Voice, "coral");
            var model = settingsStore.Get(KeyOpenAiTtsModel) ?? ResolveTtsDefault(scope, c => c.Model, "gpt-4o-mini-tts");
            var instructions = settingsStore.Get(KeyOpenAiTtsInstructions)
                               ?? ResolveTtsDefault(scope, c => c.Instructions ?? string.Empty, string.Empty);
            var purgeHours = ResolvePurgeHours(scope, settingsStore);

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
                helperText: "Used for TTS audio and AI Curated link layout");

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

            // Bottom hint bar
            helpers.WriteLine();
            helpers.WriteLine(
                $"  {palette.GetAccentFg().AnsiFg}↑↓{Reset}{palette.GetDimFg().AnsiFg}:navigate   " +
                $"{palette.GetAccentFg().AnsiFg}Enter{Reset}{palette.GetDimFg().AnsiFg}:edit   " +
                $"{palette.GetAccentFg().AnsiFg}Esc{Reset}{palette.GetDimFg().AnsiFg}:back   " +
                $"{palette.GetAccentFg().AnsiFg}:{Reset}{palette.GetDimFg().AnsiFg}:run command{Reset}");
            helpers.ClearRemainingLines();

            var command = await ctx.InputHandler.WaitForInputAsync(ct).ConfigureAwait(false);

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
                await DispatchRowAsync(ctx, options, rows[selectedIndex], ct).ConfigureAwait(false);
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
                await PodcastConfirmationScreens.PromptAndSetOutputFolderAsync(
                    ctx, store, current, ct).ConfigureAwait(false);
                return;
            }

            case SetupRow.Voice:
            {
                using var scope = ctx.ScopeFactory.CreateScope();
                var store = scope.ServiceProvider.GetRequiredService<IUserSettingsStore>();
                var current = store.Get(KeyOpenAiTtsVoice)
                              ?? ResolveTtsDefault(scope, c => c.Voice, "coral");
                await PodcastConfirmationScreens.PromptAndPickVoiceAsync(
                    ctx, options, store, current, ct).ConfigureAwait(false);
                return;
            }

            case SetupRow.Model:
            {
                using var scope = ctx.ScopeFactory.CreateScope();
                var store = scope.ServiceProvider.GetRequiredService<IUserSettingsStore>();
                var current = store.Get(KeyOpenAiTtsModel)
                              ?? ResolveTtsDefault(scope, c => c.Model, "gpt-4o-mini-tts");
                await PodcastConfirmationScreens.PromptAndPickModelAsync(
                    ctx, options, store, current, ct).ConfigureAwait(false);
                return;
            }

            case SetupRow.TtsInstructions:
                await HandleSetTtsInstructions(ctx, options, ct).ConfigureAwait(false);
                return;

            case SetupRow.AutoPurgeHours:
                await HandleSetAutoPurgeHours(ctx, options, ct).ConfigureAwait(false);
                return;
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

            Console.SetCursorPosition(2, panelRow + 7);
            Console.Write($"{palette.SecondaryText.AnsiFg}Press any key to return…{Reset}");
            try
            {
                _ = await ctx.InputHandler.WaitForInputAsync(ct).ConfigureAwait(false);
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
                    Console.SetCursorPosition(0, panelRow + i);
                }
                catch (ArgumentOutOfRangeException)
                {
                    break;
                }

                Console.Write(new string(' ', Math.Max(20, Console.WindowWidth - 1)));
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
    /// Resolves a TTS configuration default from the bound
    /// <see cref="OpenAiTtsConfiguration"/> when the settings store has no
    /// override. Falls back to the supplied literal when no options are
    /// registered (defensive: keeps the Setup screen renderable in the unit
    /// tests' bare DI container).
    /// </summary>
    private static string ResolveTtsDefault(
        IServiceScope scope,
        Func<OpenAiTtsConfiguration, string> selector,
        string fallback)
    {
        try
        {
            var opts = scope.ServiceProvider.GetService<IOptions<OpenAiTtsConfiguration>>();
            var value = opts is null ? null : selector(opts.Value);
            return string.IsNullOrEmpty(value) ? fallback : value;
        }
        catch
        {
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
            return 36;
        }
    }

    /// <summary>
    /// Truncates the middle of a long path with an ellipsis so both ends stay
    /// visible (mirrors the helper in <see cref="PodcastConfirmationScreens"/>).
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

    private static async Task HandleSetApiKey(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        var palette = BuiltInThemes.Get(ctx.ThemeProvider.CurrentTheme);
        var field = new FormFieldConfig
        {
            Label = "OpenAI API Key",
            Placeholder = "sk-...",
            HelpText = "Get a key at platform.openai.com/api-keys",
            IsSecret = true,
            Validate = v => string.IsNullOrWhiteSpace(v) ? "Key cannot be empty" : null,
        };

        var startRow = Math.Max(1, (Console.WindowHeight / 2) - 3);
        var fieldWidth = Math.Min(Console.WindowWidth - 6, 60);
        var apiKey = await FormField.PromptAsync(ctx.InputHandler, field, palette, startRow, fieldWidth, ct).ConfigureAwait(false);

        if (apiKey == null)
        {
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
            return;
        }

        var trimmedKey = apiKey.Trim();

        using var scope = ctx.ScopeFactory.CreateScope();
        var ttsService = scope.ServiceProvider.GetRequiredService<ITtsService>();
        ttsService.SetApiKeyOverride(trimmedKey);

        ctx.NavigationService.SetStatusMessage("Verifying API key...");
        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);

        var validation = await ttsService.ValidateApiKeyAsync(ct).ConfigureAwait(false);

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
                ctx.NavigationService.SetStatusMessage("API key verified but failed to save");
            }
        }
        else
        {
            ttsService.SetApiKeyOverride(string.Empty);
            ctx.NavigationService.SetStatusMessage(validation.ErrorMessage ?? "Invalid API key");
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
        string? prefilledName = null;

        while (!ct.IsCancellationRequested)
        {
            var palette = BuiltInThemes.Get(ctx.ThemeProvider.CurrentTheme);
            var startRow = Math.Max(1, (Console.WindowHeight / 2) - 3);
            var fieldWidth = Math.Min(Math.Max(40, Console.WindowWidth) - 6, 60);

            // No subtitle now — label + helptext alone keep the field at
            // FormField.Height (5 rows). The probe panel anchors directly
            // below that.
            var helpRow = startRow + FormField.Height + 1;
            var maxCopy = GcsCopy.MaxCopyWidth(fieldWidth);

            var field = new FormFieldConfig
            {
                Label = GcsCopy.FitOrShorten(
                    "Bucket name (e.g. joe-podcast-feed)",
                    "Bucket name",
                    maxCopy),
                Placeholder = GcsCopy.FitOrShorten(
                    "wirecopy-podcasts-acme",
                    "bucket-name",
                    Math.Max(10, fieldWidth - 4)),
                HelpText = GcsCopy.FitOrShorten(
                    "? for help · Enter to verify · Esc to cancel",
                    "? help · Enter verify · Esc",
                    maxCopy),
                IsSecret = false,
                MaxLength = 63,
                InitialValue = prefilledName,
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

            // Strip the optional gs:// prefix mirrored from the validator.
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
                    prefilledName = name;
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
                    Console.SetCursorPosition(0, startRow + i);
                }
                catch (ArgumentOutOfRangeException)
                {
                    break;
                }

                Console.Write(new string(' ', Math.Max(20, Console.WindowWidth - 1)));
            }
        }
    }

    /// <summary>
    /// FormField validator for the GCS bucket name. Strips an optional
    /// <c>gs://</c> prefix silently and rejects underscores, the
    /// <c>goog</c> prefix, and <c>..</c> substrings on top of the
    /// <see cref="GcsConfiguration.IsValidBucketName"/> regex check.
    /// </summary>
    internal static string? ValidateBucketNameInput(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "Bucket name cannot be empty";
        }

        var s = raw.Trim();
        if (s.StartsWith("gs://", StringComparison.Ordinal))
        {
            s = s[5..];
        }

        if (s.Length < 3)
        {
            return "Too short — bucket names must be 3-63 chars";
        }

        if (s.Length > 63)
        {
            return "Too long — bucket names must be 3-63 chars";
        }

        if (!GcsConfiguration.IsValidBucketName(s)
            || s.Contains("..", StringComparison.Ordinal)
            || s.StartsWith("goog", StringComparison.Ordinal))
        {
            return "Use only lowercase a-z, 0-9, hyphens, dots (no underscores, no caps)";
        }

        return null;
    }

    /// <summary>
    /// Strip optional <c>gs://</c> prefix and trim whitespace so callers see
    /// the bare bucket name.
    /// </summary>
    internal static string NormalizeBucketName(string raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }

        var s = raw.Trim();
        if (s.StartsWith("gs://", StringComparison.Ordinal))
        {
            s = s[5..];
        }

        return s;
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
            ctx.NavigationService.SetStatusMessage("Failed to save bucket name");
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
                Console.SetCursorPosition(0, startRow + i);
            }
            catch (ArgumentOutOfRangeException)
            {
                break;
            }

            var width = Math.Max(20, Console.WindowWidth - 1);
            Console.Write(new string(' ', width));
        }

        try
        {
            Console.SetCursorPosition(0, startRow);
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
        var fieldWidth = Math.Min(Math.Max(40, Console.WindowWidth) - 6, 60);
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

                ctx.NavigationService.SetStatusMessage(
                    string.IsNullOrEmpty(maskedEmail)
                        ? "Service account saved"
                        : $"Service account saved · {maskedEmail}");
            }
            else
            {
                ctx.NavigationService.SetStatusMessage(result.ErrorMessage ?? "Failed to save service account key");
            }
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Failed to persist service account key");
            ctx.NavigationService.SetStatusMessage("Failed to save service account key");
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
    private static async Task HandleSetTtsInstructions(
        CommandContext ctx,
        RenderOptions options,
        CancellationToken ct)
    {
        using var scope = ctx.ScopeFactory.CreateScope();
        var settingsStore = scope.ServiceProvider.GetRequiredService<IUserSettingsStore>();
        var current = settingsStore.Get(KeyOpenAiTtsInstructions)
                      ?? ResolveTtsDefault(scope, c => c.Instructions ?? string.Empty, string.Empty);

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
                ctx.NavigationService.SetStatusMessage("Failed to reset TTS instructions");
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
            ctx.NavigationService.SetStatusMessage("Failed to save TTS instructions");
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
            "Auto-purge window in hours (default 36, 'reset' to revert): ",
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
                ctx.NavigationService.SetStatusMessage("Failed to reset auto-purge window");
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
            ctx.NavigationService.SetStatusMessage("Failed to save auto-purge window");
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

            var ttsService = scope.ServiceProvider.GetRequiredService<ITtsService>();
            ttsService.SetApiKeyOverride(string.Empty);

            ctx.NavigationService.SetStatusMessage("API key cleared");
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Failed to remove API key");
            ctx.NavigationService.SetStatusMessage("Failed to clear API key");
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

            ctx.NavigationService.SetStatusMessage("Bucket name cleared");
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Failed to remove bucket name");
            ctx.NavigationService.SetStatusMessage("Failed to clear bucket name");
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

            ctx.NavigationService.SetStatusMessage("Service account key cleared");
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Failed to remove service account key");
            ctx.NavigationService.SetStatusMessage("Failed to clear service account key");
        }

        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
    }
#pragma warning restore SA1201, SA1202
}
