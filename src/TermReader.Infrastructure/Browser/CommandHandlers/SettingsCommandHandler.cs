// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TermReader.Application.DTOs.Browser;
using TermReader.Application.Interfaces;
using TermReader.Application.Interfaces.Browser;
using TermReader.Application.Interfaces.Podcast;
using TermReader.Domain.Enums.Browser;
using TermReader.Infrastructure.Browser.Themes;
using TermReader.Infrastructure.Browser.UI.Components;
using TermReader.Infrastructure.Configuration;
using TermReader.Infrastructure.Podcast;

namespace TermReader.Infrastructure.Browser.CommandHandlers;

/// <summary>
/// Handles settings, :set, and :clear commands for API keys, buckets, and configuration.
/// </summary>
internal static class SettingsCommandHandler
{
    internal static async Task HandleConfigScreen(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        const string reset = "\x1b[0m";
        var p = Infrastructure.Browser.Themes.BuiltInThemes.Get(ctx.ThemeProvider.CurrentTheme);

        using var scope = ctx.ScopeFactory.CreateScope();
        var settingsStore = scope.ServiceProvider.GetRequiredService<IUserSettingsStore>();

        while (!ct.IsCancellationRequested)
        {
            var helpers = new Infrastructure.Browser.UI.Renderers.RenderHelpers { TerminalHeight = options.TerminalHeight };
            helpers.Clear();

            var width = Math.Max(20, options.TerminalWidth - 2);
            helpers.WriteLine();
            helpers.WriteLine($"{p.HeaderBorderFg.AnsiFg}\u256d{new string('\u2500', width - 2)}\u256e{reset}");
            var title = Infrastructure.Browser.UI.Renderers.RenderHelpers.TruncateText("Settings", width - 4);
            helpers.WriteLine(
                $"{p.HeaderBorderFg.AnsiFg}\u2502 {p.HeaderTitleFg.AnsiFg}" +
                $"{title.PadRight(width - 4)}{p.HeaderBorderFg.AnsiFg} \u2502{reset}");
            helpers.WriteLine($"{p.HeaderBorderFg.AnsiFg}\u2570{new string('\u2500', width - 2)}\u256f{reset}");
            helpers.WriteLine();

            // OpenAI TTS
            var hasOpenAi = !string.IsNullOrWhiteSpace(settingsStore.Get("OpenAiApiKey"));
            var openAiIndicator = hasOpenAi
                ? $"  {p.PromptFg.AnsiFg}\u25cf{reset} OpenAI TTS          {p.PromptFg.AnsiFg}configured{reset}"
                : $"  {p.SecondaryText.AnsiFg}\u25cb{reset} OpenAI TTS          {p.SecondaryText.AnsiFg}not set{reset}";
            helpers.WriteLine(openAiIndicator);
            helpers.WriteLine($"    {p.SecondaryText.AnsiFg}Text-to-speech for podcast generation{reset}");
            helpers.WriteLine($"    {p.SecondaryText.AnsiFg}:set apikey to configure{reset}");
            helpers.WriteLine();

            // Anthropic
            var hasAnthropic = !string.IsNullOrWhiteSpace(settingsStore.Get("AnthropicApiKey"));
            var anthropicIndicator = hasAnthropic
                ? $"  {p.PromptFg.AnsiFg}\u25cf{reset} Anthropic AI        {p.PromptFg.AnsiFg}configured{reset}"
                : $"  {p.SecondaryText.AnsiFg}\u25cb{reset} Anthropic AI        {p.SecondaryText.AnsiFg}not set{reset}";
            helpers.WriteLine(anthropicIndicator);
            helpers.WriteLine($"    {p.SecondaryText.AnsiFg}AI-powered link hierarchy for better page layout{reset}");
            helpers.WriteLine($"    {p.SecondaryText.AnsiFg}:set anthropic-key to configure{reset}");
            helpers.WriteLine();

            // GCS
            var hasBucket = !string.IsNullOrWhiteSpace(settingsStore.Get("GcsBucketName"));
            var gcsIndicator = hasBucket
                ? $"  {p.PromptFg.AnsiFg}\u25cf{reset} GCS Storage         {p.PromptFg.AnsiFg}{settingsStore.Get("GcsBucketName")}{reset}"
                : $"  {p.SecondaryText.AnsiFg}\u25cb{reset} GCS Storage         {p.SecondaryText.AnsiFg}not set{reset}";
            helpers.WriteLine(gcsIndicator);
            helpers.WriteLine($"    {p.SecondaryText.AnsiFg}Cloud storage for podcast RSS feed publishing{reset}");
            helpers.WriteLine($"    {p.SecondaryText.AnsiFg}:set bucket to configure{reset}");
            helpers.WriteLine();

            helpers.WriteLine($"  {p.GetAccentFg().AnsiFg}Esc{reset}{p.SecondaryText.AnsiFg}:back   {reset}" +
                              $"{p.GetAccentFg().AnsiFg}:{reset}{p.SecondaryText.AnsiFg}run command{reset}");
            helpers.ClearRemainingLines();

            var command = await ctx.InputHandler.WaitForInputAsync(ct);

            if (command.Type == CommandType.TerminalResized)
            {
                options = ctx.GetCurrentRenderOptions();
                continue;
            }

            if (command.Type is CommandType.GoBack or CommandType.Quit)
            {
                break;
            }

            if (command.Type == CommandType.OpenCommandLine)
            {
                var input = await ctx.InputHandler.PromptForInputAsync(":", ct);
                if (!string.IsNullOrWhiteSpace(input))
                {
                    await SearchCommandHandler.HandleCommandLineInput(ctx, input.Trim(), options, ct);
                }

                continue;
            }
        }

        await ctx.RenderCurrentPageAsync(options, ct);
    }

    internal static async Task HandleSetCommand(CommandContext ctx, string? subcommand, RenderOptions options, CancellationToken ct)
    {
        var setting = subcommand?.Trim().ToLowerInvariant();

        switch (setting)
        {
            case "apikey":
                await HandleSetApiKey(ctx, options, ct);
                break;

            case "bucket":
                await HandleSetBucket(ctx, options, ct);
                break;

            case "key":
                await HandleSetKey(ctx, options, ct);
                break;

            case "anthropic-key":
                await HandleSetAnthropicKey(ctx, options, ct);
                break;

            default:
                ctx.NavigationService.SetStatusMessage("Usage: :set apikey | :set bucket | :set key | :set anthropic-key");
                await ctx.RenderCurrentPageAsync(options, ct);
                break;
        }
    }

    internal static async Task HandleClearCommand(CommandContext ctx, string? subcommand, RenderOptions options, CancellationToken ct)
    {
        var setting = subcommand?.Trim().ToLowerInvariant();

        switch (setting)
        {
            case "apikey":
                await HandleClearApiKey(ctx, options, ct);
                break;

            case "bucket":
                await HandleClearBucket(ctx, options, ct);
                break;

            case "key":
                await HandleClearKey(ctx, options, ct);
                break;

            case "anthropic-key":
                await HandleClearAnthropicKey(ctx, options, ct);
                break;

            default:
                // No recognized subcommand — delegate to existing collection clear behavior
                await CollectionCommandHandler.HandleClearCollection(ctx, options, ct);
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
        var apiKey = await FormField.PromptAsync(ctx.InputHandler, field, palette, startRow, fieldWidth, ct);

        if (apiKey == null)
        {
            await ctx.RenderCurrentPageAsync(options, ct);
            return;
        }

        var trimmedKey = apiKey.Trim();

        using var scope = ctx.ScopeFactory.CreateScope();
        var ttsService = scope.ServiceProvider.GetRequiredService<ITtsService>();
        ttsService.SetApiKeyOverride(trimmedKey);

        ctx.NavigationService.SetStatusMessage("Verifying API key...");
        await ctx.RenderCurrentPageAsync(options, ct);

        var validation = await ttsService.ValidateApiKeyAsync(ct);

        if (validation.IsValid)
        {
            try
            {
                var settingsStore = scope.ServiceProvider.GetRequiredService<IUserSettingsStore>();
                settingsStore.Set("OpenAiApiKey", trimmedKey, encrypt: true);
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

        await ctx.RenderCurrentPageAsync(options, ct);
    }

    private static async Task HandleSetBucket(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        var bucketName = await ctx.InputHandler.PromptForInputAsync(
            "GCS bucket name: ", ct);

        if (string.IsNullOrWhiteSpace(bucketName))
        {
            await ctx.RenderCurrentPageAsync(options, ct);
            return;
        }

        var trimmed = bucketName.Trim();

        if (!GcsConfiguration.IsValidBucketName(trimmed))
        {
            ctx.NavigationService.SetStatusMessage(
                $"Invalid: \"{trimmed}\" \u2014 must be 3\u201363 chars, lowercase a\u2013z/0\u20139/hyphens/dots");
            await ctx.RenderCurrentPageAsync(options, ct);
            return;
        }

        try
        {
            using var scope = ctx.ScopeFactory.CreateScope();
            var settingsStore = scope.ServiceProvider.GetRequiredService<IUserSettingsStore>();
            settingsStore.Set("GcsBucketName", trimmed);

            var gcsConfig = scope.ServiceProvider.GetRequiredService<IOptions<GcsConfiguration>>().Value;
            gcsConfig.BucketName = trimmed;

            ctx.NavigationService.SetStatusMessage($"Bucket set to \"{trimmed}\"");
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Failed to persist bucket name");
            ctx.NavigationService.SetStatusMessage("Failed to save bucket name");
        }

        await ctx.RenderCurrentPageAsync(options, ct);
    }

    private static async Task HandleSetAnthropicKey(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        var palette = BuiltInThemes.Get(ctx.ThemeProvider.CurrentTheme);
        var field = new FormFieldConfig
        {
            Label = "Anthropic API Key",
            Placeholder = "sk-ant-...",
            HelpText = "Get a key at console.anthropic.com",
            IsSecret = true,
            Validate = v =>
            {
                if (string.IsNullOrWhiteSpace(v))
                {
                    return "Key cannot be empty";
                }

                var trimmed = v.Trim();
                if (!trimmed.StartsWith("sk-ant-", StringComparison.Ordinal) &&
                    !trimmed.StartsWith("sk-", StringComparison.Ordinal))
                {
                    return "Must start with sk-ant- or sk-";
                }

                return null;
            },
        };

        var startRow = Math.Max(1, (Console.WindowHeight / 2) - 3);
        var fieldWidth = Math.Min(Console.WindowWidth - 6, 60);
        var apiKey = await FormField.PromptAsync(ctx.InputHandler, field, palette, startRow, fieldWidth, ct);

        if (apiKey == null)
        {
            await ctx.RenderCurrentPageAsync(options, ct);
            return;
        }

        var trimmedKey = apiKey.Trim();

        try
        {
            using var scope = ctx.ScopeFactory.CreateScope();
            var settingsStore = scope.ServiceProvider.GetRequiredService<IUserSettingsStore>();
            settingsStore.Set("AnthropicApiKey", trimmedKey, encrypt: true);
            ctx.NavigationService.SetStatusMessage("Anthropic API key saved");
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Failed to persist Anthropic API key");
            ctx.NavigationService.SetStatusMessage("Failed to save Anthropic API key");
        }

        await ctx.RenderCurrentPageAsync(options, ct);
    }

    private static async Task HandleSetKey(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        var palette = BuiltInThemes.Get(ctx.ThemeProvider.CurrentTheme);
        var field = new FormFieldConfig
        {
            Label = "GCS Service Account Key",
            Placeholder = "File path (e.g. ~/keys/service-account.json)",
            HelpText = "Download from console.cloud.google.com \u2192 IAM \u2192 Service Accounts",
            Validate = v =>
            {
                if (string.IsNullOrWhiteSpace(v))
                {
                    return "Path cannot be empty";
                }

                var path = v.Trim();

                // Expand ~ to home directory
                if (path.StartsWith('~'))
                {
                    var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    path = Path.Combine(home, path[1..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                }

                path = Path.GetFullPath(path);

                var result = GcsStorageClient.ValidateKeyFile(path);
                return result.IsValid ? null : (result.ErrorMessage ?? "Invalid key file");
            },
        };

        var startRow = Math.Max(1, (Console.WindowHeight / 2) - 3);
        var fieldWidth = Math.Min(Console.WindowWidth - 6, 60);
        var keyPath = await FormField.PromptAsync(ctx.InputHandler, field, palette, startRow, fieldWidth, ct);

        if (keyPath == null)
        {
            await ctx.RenderCurrentPageAsync(options, ct);
            return;
        }

        var trimmed = keyPath.Trim();

        // Expand ~ to home directory
        if (trimmed.StartsWith('~'))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            trimmed = Path.Combine(home, trimmed[1..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }

        trimmed = Path.GetFullPath(trimmed);

        try
        {
            using var scope = ctx.ScopeFactory.CreateScope();
            var cloudStorage = scope.ServiceProvider.GetRequiredService<ICloudStorageClient>();

            if (cloudStorage is GcsStorageClient gcsClient)
            {
                var result = gcsClient.SetServiceAccountKeyPath(trimmed);
                if (result.IsValid)
                {
                    ctx.NavigationService.SetStatusMessage("Service account key saved");
                }
                else
                {
                    ctx.NavigationService.SetStatusMessage(result.ErrorMessage ?? "Failed to set key");
                }
            }
            else
            {
                // Fallback: store directly in settings
                var settingsStore = scope.ServiceProvider.GetRequiredService<IUserSettingsStore>();
                settingsStore.Set("GcsServiceAccountKeyPath", trimmed, encrypt: true);
                ctx.NavigationService.SetStatusMessage("Service account key path saved");
            }
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Failed to persist service account key path");
            ctx.NavigationService.SetStatusMessage("Failed to save key path");
        }

        await ctx.RenderCurrentPageAsync(options, ct);
    }

    private static async Task HandleClearApiKey(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        try
        {
            using var scope = ctx.ScopeFactory.CreateScope();
            var settingsStore = scope.ServiceProvider.GetRequiredService<IUserSettingsStore>();
            settingsStore.Remove("OpenAiApiKey");

            var ttsService = scope.ServiceProvider.GetRequiredService<ITtsService>();
            ttsService.SetApiKeyOverride(string.Empty);

            ctx.NavigationService.SetStatusMessage("API key cleared");
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Failed to remove API key");
            ctx.NavigationService.SetStatusMessage("Failed to clear API key");
        }

        await ctx.RenderCurrentPageAsync(options, ct);
    }

    private static async Task HandleClearBucket(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        try
        {
            using var scope = ctx.ScopeFactory.CreateScope();
            var settingsStore = scope.ServiceProvider.GetRequiredService<IUserSettingsStore>();
            settingsStore.Remove("GcsBucketName");

            var gcsConfig = scope.ServiceProvider.GetRequiredService<IOptions<GcsConfiguration>>().Value;
            gcsConfig.BucketName = null;

            ctx.NavigationService.SetStatusMessage("Bucket name cleared");
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Failed to remove bucket name");
            ctx.NavigationService.SetStatusMessage("Failed to clear bucket name");
        }

        await ctx.RenderCurrentPageAsync(options, ct);
    }

    private static async Task HandleClearAnthropicKey(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        try
        {
            using var scope = ctx.ScopeFactory.CreateScope();
            var settingsStore = scope.ServiceProvider.GetRequiredService<IUserSettingsStore>();
            settingsStore.Remove("AnthropicApiKey");
            ctx.NavigationService.SetStatusMessage("Anthropic API key cleared");
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Failed to remove Anthropic API key");
            ctx.NavigationService.SetStatusMessage("Failed to clear Anthropic API key");
        }

        await ctx.RenderCurrentPageAsync(options, ct);
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
                settingsStore.Remove("GcsServiceAccountKeyPath");
            }

            ctx.NavigationService.SetStatusMessage("Service account key cleared");
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Failed to remove service account key");
            ctx.NavigationService.SetStatusMessage("Failed to clear service account key");
        }

        await ctx.RenderCurrentPageAsync(options, ct);
    }
}
