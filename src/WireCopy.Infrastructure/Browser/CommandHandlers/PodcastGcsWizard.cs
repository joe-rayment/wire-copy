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
using WireCopy.Infrastructure.Configuration;
using WireCopy.Infrastructure.Podcast;

namespace WireCopy.Infrastructure.Browser.CommandHandlers;

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

        // Phase 1: Probe GCS bucket with spinner. We deliberately route through
        // GcsBucketProbe (workspace-dwgl) so probe + create are explicit, and
        // re-create on NotFound below to preserve the wizard's previous
        // auto-bootstrap behaviour for first-run users.
        var gcsClient = cloudStorage as GcsStorageClient;
        using var validationCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var validationTask = gcsClient != null
            ? GcsBucketProbe.ProbeAsync(gcsClient, bucketName, validationCts.Token)
            : cloudStorage.ValidateConnectionAsync(bucketName, validationCts.Token);

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

        // Preserve the wizard's prior behaviour: when the probe says NotFound
        // and the configuration permits auto-create, create the bucket then
        // re-probe to validate write access (workspace-dwgl refactor).
        if (!validationResult.IsValid
            && validationResult.ErrorType == CloudStorageValidationErrorType.BucketNotFound
            && gcsConfig.CreateBucketIfNotExists
            && gcsClient != null)
        {
            try
            {
                Console.Write(
                    $"\r      {p.SecondaryText.AnsiFg}{PodcastCommandHandler.SpinnerFrames[0]} creating bucket {bucketName}...{Reset}    ");
                await GcsBucketProbe.CreateAsync(gcsClient, bucketName, gcsConfig, ct).ConfigureAwait(false);
                validationResult = await GcsBucketProbe.ProbeAsync(gcsClient, bucketName, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ctx.Logger.LogWarning(ex, "GCS bucket creation failed for {Bucket}", bucketName);
                Console.Write($"\r{new string(' ', Math.Max(20, options.TerminalWidth))}\r");
                return (false, null, false, $"Bucket creation failed: {ex.Message}");
            }
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
    /// Strips bracketed-paste markers (\x1b[200~ ... \x1b[201~) and surrounding
    /// whitespace from a user-entered key blob. Some terminals emit these markers
    /// as literal characters when bracketed-paste mode isn't fully negotiated;
    /// they must be removed before JSON parsing or path resolution.
    /// </summary>
    /// <remarks>
    /// Public for testing. Idempotent: safe to call on already-clean input.
    /// </remarks>
    internal static string SanitizeKeyInput(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        // Strip ESC[200~ (paste-start) and ESC[201~ (paste-end) anywhere in the string,
        // along with any plain bracketed-paste markers a buggy terminal might leak as
        // bare "[200~" / "[201~" without the escape.
        var cleaned = input
            .Replace("\x1b[200~", string.Empty, StringComparison.Ordinal)
            .Replace("\x1b[201~", string.Empty, StringComparison.Ordinal)
            .Replace("[200~", string.Empty, StringComparison.Ordinal)
            .Replace("[201~", string.Empty, StringComparison.Ordinal);

        return cleaned.Trim();
    }

    /// <summary>
    /// Validates and saves a key input (JSON content or file path).
    /// Bracketed-paste markers and surrounding whitespace are stripped before
    /// classification so multi-line pastes from any terminal parse correctly.
    /// </summary>
    internal static Task<(bool Saved, string? Error)> ValidateAndSaveKeyAsync(
        string input,
        GcsStorageClient gcsClient)
    {
        var sanitized = SanitizeKeyInput(input);

        if (string.IsNullOrEmpty(sanitized))
        {
            return Task.FromResult((false, (string?)"Key cannot be empty"));
        }

        ServiceAccountKeyValidationResult validation;

        if (sanitized.StartsWith('{'))
        {
            validation = gcsClient.SetServiceAccountKeyContent(sanitized);
        }
        else
        {
            var path = sanitized;
            if (path.StartsWith('~'))
            {
                path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    path[1..].TrimStart('/'));
            }

            try
            {
                path = Path.GetFullPath(path);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Sanitized blob doesn't look like JSON and isn't a usable path either —
                // surface a clear error rather than crashing or cascading.
                return Task.FromResult((false, (string?)$"Not valid JSON or file path: {ex.Message}"));
            }

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
                "A service account key lets WireCopy upload your podcast to Google Cloud Storage.",
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
                Label = "Paste your GCP service account JSON here (the full file contents starting with { and ending with }).",
                Subtitle = "Find or create one at console.cloud.google.com/iam-admin/serviceaccounts → your service account → Keys → Add Key → JSON.",
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

            var (saved, error) = await ValidateAndSaveKeyAsync(keyInput, gcsClient).ConfigureAwait(false);

            if (saved)
            {
                // Drain any stale keys from the paste before we move on to the
                // bucket step — leftover characters would otherwise pre-fill it.
                ctx.InputHandler.DrainBufferedInput();
                result = new GcsWizardResult { KeySaved = true };
                break;
            }

            // Show error below the field. Drain any stale keys from a partial paste
            // so the next iteration's prompt isn't pre-filled with leftover \r's
            // that would cascade through additional credential prompts.
            Console.SetCursorPosition(2, row + FormField.HeightFor(keyField));
            Console.Write($"{p.ErrorFg.AnsiFg}✗ {error}{Reset}");
            await Task.Delay(2000, ct).ConfigureAwait(false);
            ctx.InputHandler.DrainBufferedInput();
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
                Label = "Bucket name for your podcast feed (DNS-style, lowercase, e.g. joe-podcast-feed). Created if it doesn't exist.",
                Subtitle = "DNS-style: lowercase a-z, 0-9, hyphens. We'll create it if it doesn't exist.",
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
            var spinnerRow = row + FormField.HeightFor(bucketField);
            Console.SetCursorPosition(2, spinnerRow);
            Console.Write($"{p.GetAccentFg().AnsiFg}⡇ Validating bucket...{Reset}");

            var (bucketSuccess, bucketUrl, bucketFeedExisted, bucketError) =
                await ValidateAndBootstrapBucketAsync(ctx, options, trimmedBucket, gcsConfig, ct).ConfigureAwait(false);

            if (bucketSuccess)
            {
                gcsConfig.BucketName = trimmedBucket;
                settingsStore.Set("GcsBucketName", trimmedBucket);

                // workspace-cgnt: run the four-step verify probe (Auth →
                // Upload → Download+compare → Delete) before declaring
                // success. The probe surfaces IAM / billing / region
                // failures the GET-bucket-only check missed.
                var verifyOutcome = await RunVerifyStepAsync(
                    ctx, gcsClient, p, trimmedBucket, spinnerRow + 1, ct).ConfigureAwait(false);

                if (!verifyOutcome.Success)
                {
                    // Halt and let the user fix and retry. The verify panel
                    // already rendered the failure copy + remediation; we
                    // wait for them to acknowledge before falling back to
                    // the bucket field.
                    await Task.Delay(2000, ct).ConfigureAwait(false);
                    continue;
                }

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
    /// Drives the live four-line verify panel by polling the verify result
    /// in the background and updating each row as it completes. The panel
    /// shows authoritative success/failure with timing and remediation
    /// copy on failure (workspace-cgnt). Returns the underlying result so
    /// the caller can branch on Success.
    /// </summary>
    internal static async Task<GcsVerifyCredentialsResult> RunVerifyStepAsync(
        CommandContext ctx,
        GcsStorageClient gcsClient,
        ThemePalette p,
        string bucketName,
        int panelRow,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(gcsClient);
        ArgumentNullException.ThrowIfNull(bucketName);

        // Render an initial "all four steps queued" panel so the user sees
        // what is about to happen.
        var rows = new List<(GcsVerifyStep, bool?, TimeSpan?, string?)>();
        BucketProbePanel.RenderVerifyStatus(p, panelRow, GcsVerifyStep.Auth, rows);

        // Run the verify on a background task and tick the panel until done.
        var verifyTask = gcsClient.VerifyCredentialsAsync(bucketName, ct);
        var spinnerFrame = 0;
        var currentStep = GcsVerifyStep.Auth;
        var spinFrames = new[] { "⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏" };
        while (!verifyTask.IsCompleted && !ct.IsCancellationRequested)
        {
            BucketProbePanel.RenderVerifyStatus(p, panelRow, currentStep, rows);
            try
            {
                await Task.Delay(120, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            // Advance current-step heuristically — without a streaming API
            // we step through each phase based on elapsed wall time. This
            // is purely cosmetic; the result still carries the authoritative
            // per-step timings.
            spinnerFrame = (spinnerFrame + 1) % spinFrames.Length;
            currentStep = currentStep switch
            {
                GcsVerifyStep.Auth => GcsVerifyStep.Upload,
                GcsVerifyStep.Upload => GcsVerifyStep.Download,
                GcsVerifyStep.Download => GcsVerifyStep.Delete,
                _ => GcsVerifyStep.Delete,
            };
        }

        GcsVerifyCredentialsResult result;
        try
        {
            result = await verifyTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new GcsVerifyCredentialsResult
            {
                Success = false,
                FailureClass = GcsVerifyFailureClass.Generic,
                Message = "Verify cancelled",
            };
        }

        // Build the post-run rows table from the result so each step shows
        // its real timing and the failure (if any) is highlighted.
        rows = BuildResultRows(result);
        BucketProbePanel.RenderVerifyStatus(p, panelRow, GcsVerifyStep.None, rows);

        if (!result.Success)
        {
            // Show remediation copy underneath the four-line panel.
            var msgRow = panelRow + 5;
            Console.SetCursorPosition(2, msgRow);
            Console.Write($"{p.ErrorFg.AnsiFg}✗ {result.Message}{Reset}");
            ctx.Logger.LogWarning(
                "GCS verify failed at {Step} ({Class}): {Diag}",
                result.FailedAt,
                result.FailureClass,
                result.Diagnostic);
        }
        else
        {
            var msgRow = panelRow + 5;
            Console.SetCursorPosition(2, msgRow);
            Console.Write($"{p.GetSuccessFg().AnsiFg}✓ {result.Message}{Reset}");
        }

        return result;
    }

    /// <summary>
    /// Translates a <see cref="GcsVerifyCredentialsResult"/> into the row
    /// shape <see cref="BucketProbePanel.RenderVerifyStatus"/> expects.
    /// Steps before the failure are marked Done=true; the failed step is
    /// Done=false; steps after are left untouched (rendered as dim dots).
    /// </summary>
    internal static List<(GcsVerifyStep Step, bool? Done, TimeSpan? Elapsed, string? Note)> BuildResultRows(
        GcsVerifyCredentialsResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var list = new List<(GcsVerifyStep, bool?, TimeSpan?, string?)>();
        var stepDurations = new (GcsVerifyStep Step, TimeSpan Duration)[]
        {
            (GcsVerifyStep.Auth, result.AuthDuration),
            (GcsVerifyStep.Upload, result.UploadDuration),
            (GcsVerifyStep.Download, result.DownloadDuration),
            (GcsVerifyStep.Delete, result.DeleteDuration),
        };

        foreach (var (step, dur) in stepDurations)
        {
            if (result.Success)
            {
                list.Add((step, true, dur, null));
                continue;
            }

            if (step == result.FailedAt)
            {
                list.Add((step, false, dur, result.FailureClass.ToString()));
                break;
            }

            list.Add((step, true, dur, null));
        }

        return list;
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
                    "Click \"Create Service Account\", name it \"wirecopy-podcast\""),
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
