// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TermReader.Application.DTOs.Browser;
using TermReader.Application.DTOs.Podcast;
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
/// GCS service-account key + bucket setup wizard and validation helpers
/// extracted from PodcastCommandHandler.
/// </summary>
internal static class PodcastGcsWizard
{
    private const string Reset = "\x1b[0m";

    internal static async Task<(bool Success, string? FeedUrl, bool FeedExisted, string? Error)> ValidateAndBootstrapBucketAsync(
        CommandContext ctx,
        RenderOptions options,
        string bucketName,
        GcsConfiguration gcsConfig,
        CancellationToken ct)
    {
        using var scope = ctx.ScopeFactory.CreateScope();
        var cloudStorage = scope.ServiceProvider.GetRequiredService<ICloudStorageClient>();
        var publisher = scope.ServiceProvider.GetRequiredService<IPodcastPublisher>();
        var podcastConfig = scope.ServiceProvider
            .GetRequiredService<IOptions<PodcastConfiguration>>().Value;

        var p = BuiltInThemes.Get(ctx.ThemeProvider.CurrentTheme);

        // Phase 1: Validate GCS connection with spinner
        using var validationCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var validationTask = cloudStorage.ValidateConnectionAsync(bucketName, validationCts.Token);

        var spinnerFrame = 0;
        Task<NavigationCommand>? pendingKeyTask = null;

        while (!ct.IsCancellationRequested)
        {
            var spinner = PodcastCommandHandler.SpinnerFrames[spinnerFrame % PodcastCommandHandler.SpinnerFrames.Length];
            Console.Write($"\r      {p.SecondaryText.AnsiFg}{spinner} verifying {bucketName}...{Reset}    ");

            pendingKeyTask ??= ctx.InputHandler.WaitForInputAsync(ct);
            using var tickCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var tickTask = Task.Delay(PodcastCommandHandler.SpinnerIntervalMs, tickCts.Token);
            var completed = await Task.WhenAny(validationTask, pendingKeyTask, tickTask).ConfigureAwait(false);
            await tickCts.CancelAsync().ConfigureAwait(false);

            if (completed == pendingKeyTask)
            {
                var command = await pendingKeyTask.ConfigureAwait(false);
                pendingKeyTask = null;

                if (command.Type == CommandType.TerminalResized)
                {
                    options = ctx.GetCurrentRenderOptions();
                    continue;
                }

                if (command.Type is CommandType.GoBack or CommandType.Quit)
                {
                    await validationCts.CancelAsync().ConfigureAwait(false);
                    try
                    {
                        await validationTask.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // Expected when cancelling validation
                    }

                    Console.Write($"\r{new string(' ', Math.Max(20, options.TerminalWidth))}\r");
                    return (false, null, false, null);
                }
            }
            else if (completed == validationTask)
            {
                break;
            }
            else
            {
                spinnerFrame++;
            }
        }

        CloudStorageValidationResult validationResult;
        try
        {
            validationResult = await validationTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Console.Write($"\r{new string(' ', Math.Max(20, options.TerminalWidth))}\r");
            return (false, null, false, null);
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "GCS bucket validation failed unexpectedly");
            Console.Write($"\r{new string(' ', Math.Max(20, options.TerminalWidth))}\r");
            return (false, null, false, $"Bucket validation failed: {ex.Message}");
        }

        if (!validationResult.IsValid)
        {
            Console.Write($"\r{new string(' ', Math.Max(20, options.TerminalWidth))}\r");
            return (false, null, false, validationResult.ErrorMessage ?? "Bucket validation failed");
        }

        // Phase 2: Bootstrap feed
        Console.Write(
            $"\r      {p.SecondaryText.AnsiFg}{PodcastCommandHandler.SpinnerFrames[0]} setting up feed...{Reset}    ");

        var metadata = new Domain.ValueObjects.Podcast.PodcastMetadata
        {
            Title = podcastConfig.Title,
            Description = podcastConfig.Description,
            Author = podcastConfig.Author,
            Language = podcastConfig.Language,
            ImageUrl = podcastConfig.ImageUrl ?? string.Empty,
            Category = podcastConfig.Category,
            Explicit = podcastConfig.Explicit,
        };

        // Temporarily set bucket name for the publisher to use
        var previousBucketName = gcsConfig.BucketName;
        gcsConfig.BucketName = bucketName;

        try
        {
            // Check if feed already exists before bootstrapping
            var existingUrl = await publisher.GetExistingFeedUrlAsync(podcastConfig.Title, ct).ConfigureAwait(false);
            var feedExisted = existingUrl != null;

            var feedResult = await publisher.BootstrapFeedAsync(metadata, ct).ConfigureAwait(false);
            Console.Write($"\r{new string(' ', Math.Max(20, options.TerminalWidth))}\r");

            if (feedResult.Success)
            {
                return (true, feedResult.FeedUrl, feedExisted, null);
            }

            // Revert since bootstrap failed
            gcsConfig.BucketName = previousBucketName;
            return (false, null, false, feedResult.ErrorMessage ?? "Feed setup failed");
        }
        catch (Exception ex)
        {
            gcsConfig.BucketName = previousBucketName;
            ctx.Logger.LogWarning(ex, "Feed bootstrap failed for bucket {Bucket}", bucketName);
            Console.Write($"\r{new string(' ', Math.Max(20, options.TerminalWidth))}\r");
            return (false, null, false, $"Feed setup failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Validates and saves a key input (JSON content or file path).
    /// </summary>
    internal static Task<(bool Saved, string? Error)> ValidateAndSaveKeyAsync(
        string input,
        GcsStorageClient gcsClient)
    {
        ServiceAccountKeyValidationResult validation;

        if (input.StartsWith('{'))
        {
            validation = gcsClient.SetServiceAccountKeyContent(input);
        }
        else
        {
            var path = input;
            if (path.StartsWith('~'))
            {
                path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    path[1..].TrimStart('/'));
            }

            path = Path.GetFullPath(path);
            validation = GcsStorageClient.ValidateKeyFile(path);
            if (validation.IsValid)
            {
                gcsClient.SetServiceAccountKeyPath(path);
            }
        }

        return Task.FromResult(validation.IsValid
            ? (true, (string?)null)
            : (false, validation.ErrorMessage ?? "Invalid key"));
    }

    /// <summary>
    /// Multi-step wizard for first-time GCS service account key setup.
    /// Step 1: Ask if user has a key (y/n/help)
    /// Step 2: Accept key input (paste JSON or file path)
    /// Step 3: Auto-chain to bucket setup if bucket not yet configured
    /// </summary>
    internal static async Task<GcsWizardResult> RunGcsKeyWizardAsync(
        CommandContext ctx,
        RenderOptions options,
        ThemePalette p,
        GcsStorageClient gcsClient,
        GcsConfiguration gcsConfig,
        IUserSettingsStore settingsStore,
        CancellationToken ct)
    {
        var result = new GcsWizardResult();
        var fieldWidth = Math.Min(Math.Max(40, Console.WindowWidth) - 6, 60);

        // --- Step 1: Do you have a key? (choice screen) ---
        while (!ct.IsCancellationRequested)
        {
            Console.Clear();
            var row = RenderWizardStepHeader(
                p,
                "GCS Setup",
                1,
                3,
                "A service account key lets TermReader upload your podcast to Google Cloud Storage.",
                fieldWidth);
            row++;

            Console.SetCursorPosition(4, row);
            Console.Write($"{p.PrimaryText.AnsiFg}Do you have a GCP service account JSON key?{Reset}");
            row += 2;

            Console.SetCursorPosition(6, row);
            Console.Write($"{p.GetAccentFg().AnsiFg}y{p.SecondaryText.AnsiFg} — Yes, I have a JSON key ready{Reset}");
            row++;
            Console.SetCursorPosition(6, row);
            Console.Write($"{p.GetAccentFg().AnsiFg}n{p.SecondaryText.AnsiFg} — No, show me how to create one{Reset}");
            row++;
            Console.SetCursorPosition(6, row);
            Console.Write($"{p.GetAccentFg().AnsiFg}Esc{p.SecondaryText.AnsiFg} — Cancel{Reset}");

            var command = await ctx.InputHandler.WaitForInputAsync(ct).ConfigureAwait(false);

            if (command.Type == CommandType.TerminalResized)
            {
                options = ctx.GetCurrentRenderOptions();
                fieldWidth = Math.Min(Math.Max(40, Console.WindowWidth) - 6, 60);
                continue;
            }

            if (command.Type is CommandType.GoBack or CommandType.Quit)
            {
                return result;
            }

            if (command.RawKeyChar is 'y' or 'Y')
            {
                break; // Proceed to step 2
            }

            if (command.RawKeyChar is 'n' or 'N')
            {
                await ShowGcsInstructionsAsync(ctx, p, ct).ConfigureAwait(false);
                continue;
            }
        }

        // --- Step 2: Enter key (FormField with async validation) ---
        while (!ct.IsCancellationRequested)
        {
            Console.Clear();
            var row = RenderWizardStepHeader(
                p,
                "GCS Setup",
                2,
                3,
                "Paste the JSON content (Cmd/Ctrl+V) or type a file path like ~/keys/sa.json",
                fieldWidth);
            row++;

            var keyField = new FormFieldConfig
            {
                Label = "Service Account Key",
                Placeholder = "Paste JSON content here, or type a file path",
                HelpText = "JSON starts with { · paths support ~/ for your home dir",
                MaxLength = 8192,
                Validate = v => string.IsNullOrWhiteSpace(v) ? "Key cannot be empty" : null,
            };

            var keyInput = await FormField.PromptAsync(ctx.InputHandler, keyField, p, row, fieldWidth, ct).ConfigureAwait(false);
            if (keyInput == null)
            {
                return result; // Cancelled
            }

            var (saved, error) = await ValidateAndSaveKeyAsync(keyInput.Trim(), gcsClient).ConfigureAwait(false);

            if (saved)
            {
                result = new GcsWizardResult { KeySaved = true };
                break;
            }

            // Show error below the field, wait briefly, then retry
            Console.SetCursorPosition(2, row + FormField.Height);
            Console.Write($"{p.ErrorFg.AnsiFg}✗ {error}{Reset}");
            await Task.Delay(2000, ct).ConfigureAwait(false);
        }

        if (!result.KeySaved)
        {
            return result;
        }

        // --- Step 3: Bucket setup (auto-chain if not configured) ---
        if (!string.IsNullOrWhiteSpace(gcsConfig.BucketName))
        {
            var (success, url, feedExisted, bucketErr) = await ValidateAndBootstrapBucketAsync(
                ctx, options, gcsConfig.BucketName, gcsConfig, ct).ConfigureAwait(false);
            return new GcsWizardResult
            {
                KeySaved = true,
                BucketSaved = success,
                FeedUrl = url,
                FeedStatusNote = feedExisted ? "Existing feed found" : "New feed created",
                BucketError = bucketErr,
            };
        }

        while (!ct.IsCancellationRequested)
        {
            Console.Clear();
            var row = RenderWizardStepHeader(
                p, "GCS Setup", 3, 3, "Where should your podcast RSS feed be hosted?", fieldWidth);

            // Show success from previous step
            row++;
            Console.SetCursorPosition(4, row);
            Console.Write($"{p.GetSuccessFg().AnsiFg}✔{Reset} Service account key saved");
            row += 2;

            var bucketField = new FormFieldConfig
            {
                Label = "Bucket Name",
                Placeholder = "my-podcast-feed",
                HelpText = "Esc to skip (set later with :set bucket)",
                Validate = v =>
                {
                    if (string.IsNullOrWhiteSpace(v))
                    {
                        return "Bucket name cannot be empty";
                    }

                    return !GcsConfiguration.IsValidBucketName(v.Trim())
                        ? "Must be 3–63 chars: lowercase a–z, 0–9, hyphens, dots"
                        : null;
                },
            };

            var bucketInput = await FormField.PromptAsync(ctx.InputHandler, bucketField, p, row, fieldWidth, ct).ConfigureAwait(false);
            if (bucketInput == null)
            {
                return new GcsWizardResult { KeySaved = true }; // Skipped
            }

            var trimmedBucket = bucketInput.Trim();

            // Show spinner during async validation
            var spinnerRow = row + FormField.Height;
            Console.SetCursorPosition(2, spinnerRow);
            Console.Write($"{p.GetAccentFg().AnsiFg}⡇ Validating bucket...{Reset}");

            var (bucketSuccess, bucketUrl, bucketFeedExisted, bucketError) =
                await ValidateAndBootstrapBucketAsync(ctx, options, trimmedBucket, gcsConfig, ct).ConfigureAwait(false);

            if (bucketSuccess)
            {
                gcsConfig.BucketName = trimmedBucket;
                settingsStore.Set("GcsBucketName", trimmedBucket);

                return new GcsWizardResult
                {
                    KeySaved = true,
                    BucketSaved = true,
                    FeedUrl = bucketUrl,
                    FeedStatusNote = bucketFeedExisted ? "Existing feed found" : "New feed created",
                };
            }

            // Show error and retry
            Console.SetCursorPosition(2, spinnerRow);
            Console.Write($"{p.ErrorFg.AnsiFg}✗ {bucketError}{Reset}" + new string(' ', 20));
            await Task.Delay(2000, ct).ConfigureAwait(false);
        }

        return new GcsWizardResult { KeySaved = true };
    }

    /// <summary>
    /// Renders a wizard step header with title, step indicator, and optional description.
    /// Returns the next available row after the header.
    /// </summary>
    internal static int RenderWizardStepHeader(
        ThemePalette p, string title, int step, int totalSteps, string? description, int fieldWidth)
    {
        var stepIndicator = totalSteps > 0 ? $" ─ Step {step} of {totalSteps} " : " ";
        var titlePart = $"─ {title} ";
        var headerContent = titlePart + stepIndicator;
        var remainingRule = Math.Max(0, fieldWidth - headerContent.Length - 2);

        Console.SetCursorPosition(2, 1);
        Console.Write(
            $"{p.HeaderBorderFg.AnsiFg}╭{headerContent}" +
            $"{new string('─', remainingRule)}╮{Reset}");

        var row = 2;
        if (description != null)
        {
            var desc = description.Length > fieldWidth - 4
                ? description[..(fieldWidth - 4)]
                : description;
            Console.SetCursorPosition(2, row);
            Console.Write(
                $"{p.HeaderBorderFg.AnsiFg}│ {p.SecondaryText.AnsiFg}" +
                $"{desc.PadRight(fieldWidth - 4)}" +
                $"{p.HeaderBorderFg.AnsiFg} │{Reset}");
            row++;
        }

        Console.SetCursorPosition(2, row);
        Console.Write($"{p.HeaderBorderFg.AnsiFg}╰{new string('─', fieldWidth - 2)}╯{Reset}");
        return row + 1;
    }

    /// <summary>
    /// Shows GCS setup instructions when user doesn't have a key yet.
    /// </summary>
    internal static async Task ShowGcsInstructionsAsync(
        CommandContext ctx,
        ThemePalette p,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Console.Clear();
            var fieldWidth = Math.Min(Math.Max(40, Console.WindowWidth) - 6, 60);
            var row = RenderWizardStepHeader(p, "Creating a GCS Service Account Key", 0, 0, null, fieldWidth);
            row++;

            var steps = new[]
            {
                ("Go to console.cloud.google.com", "Create a project if you don't have one"),
                ("Navigate to IAM & Admin → Service Accounts",
                    "Click \"Create Service Account\", name it \"termreader-podcast\""),
                ("Grant the role: Storage Object Admin",
                    "This allows creating and managing objects in your bucket"),
                ("Go to the service account → Keys tab",
                    "Click \"Add Key\" → \"Create new key\" → JSON"),
            };

            for (var i = 0; i < steps.Length; i++)
            {
                Console.SetCursorPosition(4, row);
                Console.Write($"{p.PrimaryText.AnsiFg}{i + 1}.{Reset} {p.PrimaryText.AnsiFg}{steps[i].Item1}{Reset}");
                row++;
                Console.SetCursorPosition(7, row);
                Console.Write($"{p.SecondaryText.AnsiFg}{steps[i].Item2}{Reset}");
                row += 2;
            }

            Console.SetCursorPosition(4, row);
            Console.Write($"{p.SecondaryText.AnsiFg}Press any key to go back{Reset}");

            var command = await ctx.InputHandler.WaitForInputAsync(ct).ConfigureAwait(false);

            if (command.Type == CommandType.TerminalResized)
            {
                continue;
            }

            return;
        }
    }

    /// <summary>
    /// Result of the GCS key setup wizard.
    /// </summary>
    internal sealed class GcsWizardResult
    {
        public bool KeySaved { get; init; }

        public bool BucketSaved { get; init; }

        public string? FeedUrl { get; init; }

        public string? FeedStatusNote { get; init; }

        public string? BucketError { get; init; }
    }
}
