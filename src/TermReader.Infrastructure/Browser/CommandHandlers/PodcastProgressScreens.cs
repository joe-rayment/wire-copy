// Licensed under the MIT License. See LICENSE in the repository root.

using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TermReader.Application.DTOs.Browser;
using TermReader.Application.DTOs.Podcast;
using TermReader.Application.Interfaces.Podcast;
using TermReader.Domain.Enums.Browser;
using TermReader.Infrastructure.Browser.Themes;
using TermReader.Infrastructure.Browser.UI.Animations;
using TermReader.Infrastructure.Browser.UI.Components;
using TermReader.Infrastructure.Browser.UI.Renderers;
using TermReader.Infrastructure.Configuration;

namespace TermReader.Infrastructure.Browser.CommandHandlers;

/// <summary>
/// Progress, completion, and error screens for podcast generation,
/// extracted from PodcastCommandHandler.
/// </summary>
internal static class PodcastProgressScreens
{
    private const string Reset = PodcastCommandHandler.Reset;

    internal static async Task<PodcastResult?> ShowProgressScreenAsync(
        CommandContext ctx,
        RenderOptions options,
        Domain.Entities.Collections.Collection collection,
        IPodcastOrchestrator orchestrator,
        CancellationToken ct)
    {
        var articleCount = collection.Items.Count;
        var statuses = new PodcastCommandHandler.ArticleStatus[articleCount];
        for (var i = 0; i < articleCount; i++)
        {
            statuses[i] = new PodcastCommandHandler.ArticleStatus
            {
                Title = collection.Items[i].Title,
                State = PodcastCommandHandler.ArticleState.Pending,
            };
        }

        // Signal generating state so the CTA renders with progress bar
        ctx.IsPodcastGenerating = true;
        ctx.PodcastGenerationProgress = 0.0;

        PodcastProgress? latestProgress = null;
        var animFrame = 0;

        var lastProcessingIndex = -1;
        var lastPhase = PodcastPhase.CachingContent;
        var progress = new Progress<PodcastProgress>(p =>
        {
            Volatile.Write(ref latestProgress, p);

            // Update shared progress for CTA rendering
            ctx.PodcastGenerationProgress = Math.Clamp(p.PercentComplete / 100.0, 0.0, 1.0);

            // Phase transition: reset article statuses when moving to GeneratingAudio
            if (p.Phase == PodcastPhase.GeneratingAudio && lastPhase == PodcastPhase.CachingContent)
            {
                for (var i = 0; i < articleCount; i++)
                {
                    if (statuses[i].State != PodcastCommandHandler.ArticleState.Failed)
                    {
                        statuses[i].State = PodcastCommandHandler.ArticleState.Pending;
                        statuses[i].Method = null;
                    }
                }

                lastProcessingIndex = -1;
            }

            lastPhase = p.Phase;

            // CachingContent phase: track per-article content loading
            if (p.Phase == PodcastPhase.CachingContent && p.CurrentArticle > 0 && p.CurrentArticle <= articleCount)
            {
                var idx = p.CurrentArticle - 1;

                if (p.IsArticleComplete)
                {
                    statuses[idx].State = p.IsArticleSuccess ? PodcastCommandHandler.ArticleState.Completed : PodcastCommandHandler.ArticleState.Failed;
                    statuses[idx].Method = null;
                }
                else
                {
                    statuses[idx].State = PodcastCommandHandler.ArticleState.Processing;
                    statuses[idx].Method = p.ExtractionMethod;
                }
            }

            // GeneratingAudio phase: track per-article audio generation
            if (p.Phase == PodcastPhase.GeneratingAudio && p.CurrentArticle > 0 && p.CurrentArticle <= articleCount)
            {
                var idx = p.CurrentArticle - 1;

                if (lastProcessingIndex >= 0 && lastProcessingIndex < idx &&
                    statuses[lastProcessingIndex].State == PodcastCommandHandler.ArticleState.Processing)
                {
                    statuses[lastProcessingIndex].State = PodcastCommandHandler.ArticleState.Completed;
                }

                statuses[idx].State = p.IsFromCache ? PodcastCommandHandler.ArticleState.Cached : PodcastCommandHandler.ArticleState.Processing;
                lastProcessingIndex = idx;
            }
        });

        using var genCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var generationTask = orchestrator.GeneratePodcastAsync(collection, progress, genCts.Token);

        Task<NavigationCommand>? pendingKeyTask = null;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var palette = BuiltInThemes.Get(ctx.ThemeProvider.CurrentTheme);
                var helpers = new RenderHelpers { TerminalHeight = options.TerminalHeight };
                helpers.Clear();

                var currentProgress = Volatile.Read(ref latestProgress);
                RenderProgressContent(
                    helpers,
                    palette,
                    currentProgress,
                    animFrame,
                    statuses,
                    options.TerminalWidth,
                    options.TerminalHeight);
                helpers.ClearRemainingLines();

                pendingKeyTask ??= ctx.InputHandler.WaitForInputAsync(ct);
                var tickCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                Task completed;
                try
                {
                    var tickTask = Task.Delay(PodcastCommandHandler.AnimationIntervalMs, tickCts.Token);
                    completed = await Task.WhenAny(pendingKeyTask, generationTask, tickTask).ConfigureAwait(false);
                    await tickCts.CancelAsync().ConfigureAwait(false);
                }
                finally
                {
                    tickCts.Dispose();
                }

                if (completed == pendingKeyTask)
                {
                    var command = await pendingKeyTask.ConfigureAwait(false);
                    pendingKeyTask = null;

                    if (command.Type == CommandType.TerminalResized)
                    {
                        options = ctx.GetCurrentRenderOptions();
                        continue;
                    }

                    if (command.Type == CommandType.GoBack)
                    {
                        // If generation already completed, fall through to collect result
                        if (generationTask.IsCompleted)
                        {
                            break;
                        }

                        var shouldCancel = await ShowCancellationConfirmAsync(ctx, ct).ConfigureAwait(false);
                        if (shouldCancel)
                        {
                            await genCts.CancelAsync().ConfigureAwait(false);
                            try
                            {
                                await generationTask.ConfigureAwait(false);
                            }
                            catch (OperationCanceledException)
                            {
                                // Expected
                            }

                            return null;
                        }
                    }
                }
                else if (completed == generationTask)
                {
                    break;
                }
                else
                {
                    animFrame = (animFrame + 1) % PodcastCommandHandler.AnimationFrames.Length;
                }
            }

            // Collect the result from the completed generation task
            if (generationTask.IsCompleted)
            {
                try
                {
                    var result = await generationTask.ConfigureAwait(false);

                    for (var i = 0; i < statuses.Length; i++)
                    {
                        if (statuses[i].State == PodcastCommandHandler.ArticleState.Processing)
                        {
                            statuses[i].State = PodcastCommandHandler.ArticleState.Completed;
                        }
                    }

                    return result;
                }
                catch (OperationCanceledException)
                {
                    return null;
                }
                catch (Exception ex)
                {
                    ctx.Logger.LogError(ex, "Podcast generation failed");
                    return PodcastResult.Failure(ex.Message);
                }
            }
        }
        finally
        {
            // Clear generating state so the CTA returns to normal
            ctx.IsPodcastGenerating = false;
            ctx.PodcastGenerationProgress = 0.0;

            if (!generationTask.IsCompleted)
            {
                await genCts.CancelAsync().ConfigureAwait(false);
                try
                {
                    await generationTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected
                }
                catch (Exception ex)
                {
                    ctx.Logger.LogWarning(ex, "Generation task faulted during cleanup: {Message}", ex.Message);
                }
            }
        }

        return null;
    }

    internal static async Task<bool> ShowCancellationConfirmAsync(
        CommandContext ctx,
        CancellationToken ct)
    {
        var response = await ctx.InputHandler.PromptForInputAsync(
            "Cancel podcast generation? (y/n): ", ct).ConfigureAwait(false);
        return string.Equals(response, "y", StringComparison.OrdinalIgnoreCase);
    }

    internal static async Task ShowCompletionScreenAsync(
        CommandContext ctx,
        RenderOptions options,
        PodcastResult result,
        CancellationToken ct)
    {
        // Play celebration animation before showing the completion screen
        try
        {
            using var animScope = ctx.ScopeFactory.CreateScope();
            var browserConfig = animScope.ServiceProvider
                .GetRequiredService<IOptions<BrowserConfiguration>>().Value;

            if (!browserConfig.DisableAnimations)
            {
                var celebrationMessage = CelebrationAnimation.BuildMessage(
                    result.ArticlesProcessed, result.TotalDuration);
                CelebrationAnimation.Play(
                    BuiltInThemes.Get(ctx.ThemeProvider.CurrentTheme),
                    celebrationMessage,
                    options.TerminalWidth,
                    options.TerminalHeight);
            }
        }
        catch (Exception ex)
        {
            // Animation failure is non-fatal — proceed to the completion screen
            ctx.Logger.LogDebug(ex, "Celebration animation failed (non-fatal)");
        }

        if (!string.IsNullOrEmpty(result.FeedUrl))
        {
            CopyToClipboardOsc52(result.FeedUrl);
        }

        var scrollOffset = 0;

        while (!ct.IsCancellationRequested)
        {
            var p = BuiltInThemes.Get(ctx.ThemeProvider.CurrentTheme);
            var helpers = new RenderHelpers { TerminalHeight = options.TerminalHeight };
            helpers.Clear();

            var width = Math.Max(20, options.TerminalWidth - 2);

            // Build all content lines, then render a scrollable window
            var lines = BuildCompletionLines(p, result, width);

            // Reserve 2 lines for key hints at bottom
            var viewportHeight = options.TerminalHeight - 2;
            var maxScroll = Math.Max(0, lines.Count - viewportHeight);
            scrollOffset = Math.Clamp(scrollOffset, 0, maxScroll);

            var visibleCount = Math.Min(lines.Count - scrollOffset, viewportHeight);
            for (var i = 0; i < visibleCount; i++)
            {
                helpers.WriteLine(lines[scrollOffset + i]);
            }

            // Pad remaining viewport
            for (var i = visibleCount; i < viewportHeight; i++)
            {
                helpers.WriteLine();
            }

            // Key hints
            var hints = $"  {p.GetAccentFg().AnsiFg}Enter{Reset}{p.SecondaryText.AnsiFg}:back{Reset}";
            if (maxScroll > 0)
            {
                hints += $"   {p.GetAccentFg().AnsiFg}j/k{Reset}{p.SecondaryText.AnsiFg}:scroll{Reset}";
            }

            helpers.WriteLine(hints);
            helpers.ClearRemainingLines();

            var command = await ctx.InputHandler.WaitForInputAsync(ct).ConfigureAwait(false);

            if (command.Type == CommandType.TerminalResized)
            {
                options = ctx.GetCurrentRenderOptions();
                continue;
            }

            if (command.Type is CommandType.ActivateLink or CommandType.GoBack or CommandType.Quit)
            {
                break;
            }

            if (command.Type == CommandType.MoveDown)
            {
                scrollOffset = Math.Min(scrollOffset + 1, maxScroll);
            }
            else if (command.Type == CommandType.MoveUp)
            {
                scrollOffset = Math.Max(scrollOffset - 1, 0);
            }
            else if (command.Type == CommandType.PageDown)
            {
                scrollOffset = Math.Min(scrollOffset + (viewportHeight / 2), maxScroll);
            }
            else if (command.Type == CommandType.PageUp)
            {
                scrollOffset = Math.Max(scrollOffset - (viewportHeight / 2), 0);
            }
        }
    }

    internal static List<string> BuildCompletionLines(
        ThemePalette p,
        PodcastResult result,
        int width)
    {
        var lines = new List<string>();

        // Box header
        lines.Add(string.Empty);
        lines.Add($"{p.HeaderBorderFg.AnsiFg}╭{new string('─', width - 2)}╮{Reset}");
        var boxTitle = RenderHelpers.TruncateText("Podcast Ready!", width - 4);
        lines.Add(
            $"{p.HeaderBorderFg.AnsiFg}│ {p.HeaderTitleFg.AnsiFg}" +
            $"{boxTitle.PadRight(width - 4)}{p.HeaderBorderFg.AnsiFg} │{Reset}");
        lines.Add($"{p.HeaderBorderFg.AnsiFg}╰{new string('─', width - 2)}╯{Reset}");
        lines.Add(string.Empty);

        // --- Summary ---
        var duration = FormatDuration(result.TotalDuration);
        var fileSize = FormatFileSize(result.FileSizeBytes);
        lines.Add(
            $"  {p.HeaderTitleFg.AnsiFg}♫{Reset} {p.PrimaryText.AnsiFg}" +
            $"{result.ArticlesProcessed} articles »»» {duration} »»» {fileSize}{Reset}");

        if (result.ArticlesCached > 0)
        {
            lines.Add(
                $"    {p.SecondaryText.AnsiFg}{result.ArticlesCached} from cache{Reset}");
        }

        if (result.TotalCost > 0)
        {
            lines.Add(
                $"    {p.SecondaryText.AnsiFg}API cost: ${result.TotalCost:F4}{Reset}");
        }

        if (result.ArticlesFailed > 0)
        {
            lines.Add(
                $"    {p.ErrorFg.AnsiFg}{result.ArticlesFailed} article(s) failed{Reset}");
        }

        lines.Add(string.Empty);

        // --- File ---
        if (!string.IsNullOrEmpty(result.LocalFilePath))
        {
            lines.Add($"  {p.SecondaryText.AnsiFg}File{Reset}");
            lines.Add($"    {p.PrimaryText.AnsiFg}{result.LocalFilePath}{Reset}");
            lines.Add(string.Empty);
        }

        // --- Feed + subscription instructions ---
        if (!string.IsNullOrEmpty(result.FeedUrl))
        {
            lines.Add($"  {p.SecondaryText.AnsiFg}Feed{Reset}");
            lines.Add($"    {p.PromptFg.AnsiFg}{result.FeedUrl}{Reset}");
            lines.Add($"    {p.SecondaryText.AnsiFg}Feed URL copied to clipboard{Reset}");
            lines.Add(string.Empty);
            lines.Add($"  {p.SecondaryText.AnsiFg}What's next{Reset}");
            lines.Add($"    {p.PrimaryText.AnsiFg}1.{Reset} {p.PrimaryText.AnsiFg}Subscribe in your podcast app{Reset}");
            lines.Add($"       {p.SecondaryText.AnsiFg}Apple Podcasts / Overcast → Add show by URL → paste{Reset}");
            lines.Add($"       {p.SecondaryText.AnsiFg}Pocket Casts → Search → \"Add by URL\" → paste{Reset}");
            lines.Add($"       {p.SecondaryText.AnsiFg}Any RSS reader → Add feed → paste{Reset}");
            lines.Add($"    {p.PrimaryText.AnsiFg}2.{Reset} {p.PrimaryText.AnsiFg}Take a walk{Reset}");
            lines.Add($"       {p.SecondaryText.AnsiFg}Next time you generate, subscribe first and go do{Reset}");
            lines.Add($"       {p.SecondaryText.AnsiFg}something else. The episode will be waiting for you.{Reset}");
        }
        else
        {
            // Local-only instructions
            lines.Add($"  {p.SecondaryText.AnsiFg}Listen{Reset}");
            lines.Add($"    {p.PrimaryText.AnsiFg}VLC{Reset}{p.SecondaryText.AnsiFg} — File → Open, supports chapters{Reset}");
            lines.Add($"    {p.PrimaryText.AnsiFg}Apple Books{Reset}{p.SecondaryText.AnsiFg} — drag M4B file into library{Reset}");
            lines.Add(string.Empty);
            lines.Add($"    {p.SecondaryText.AnsiFg}Configure a GCS bucket to publish as RSS feed{Reset}");
        }

        lines.Add(string.Empty);

        return lines;
    }

    internal static async Task ShowErrorScreenAsync(
        CommandContext ctx,
        RenderOptions options,
        string errorMessage,
        IReadOnlyList<ArticleFailure> failedArticles,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var p = BuiltInThemes.Get(ctx.ThemeProvider.CurrentTheme);
            var helpers = new RenderHelpers { TerminalHeight = options.TerminalHeight };
            helpers.Clear();

            var width = Math.Max(20, options.TerminalWidth - 2);
            PodcastCommandHandler.RenderBox(helpers, p, "Podcast Error", width);
            helpers.WriteLine();

            // Error summary
            var wrappedLines = RenderHelpers.WrapText(errorMessage, width - 4);
            foreach (var line in wrappedLines)
            {
                helpers.WriteLine($"  {p.ErrorFg.AnsiFg}{line}{Reset}");
            }

            helpers.WriteLine();

            // Per-article failure details
            if (failedArticles.Count > 0)
            {
                helpers.WriteLine($"  {p.SecondaryText.AnsiFg}Failed articles:{Reset}");

                var maxArticleLines = Math.Max(1,
                    options.TerminalHeight - helpers.LinesWritten - 10);
                var displayCount = Math.Min(failedArticles.Count, maxArticleLines);

                for (var i = 0; i < displayCount; i++)
                {
                    var failure = failedArticles[i];
                    var displayTitle = RenderHelpers.TruncateText(failure.Title, width - 8);
                    helpers.WriteLine(
                        $"    {p.ErrorFg.AnsiFg}✗{Reset} {p.PrimaryText.AnsiFg}{displayTitle}{Reset}");
                    var reason = RenderHelpers.TruncateText(failure.Reason, width - 10);
                    helpers.WriteLine(
                        $"      {p.SecondaryText.AnsiFg}{reason}{Reset}");
                }

                if (failedArticles.Count > displayCount)
                {
                    helpers.WriteLine(
                        $"    {p.SecondaryText.AnsiFg}... and {failedArticles.Count - displayCount} more{Reset}");
                }

                helpers.WriteLine();
            }

            // Actionable suggestions based on error type
            helpers.WriteLine($"  {p.SecondaryText.AnsiFg}What to try:{Reset}");
            foreach (var suggestion in GetSuggestionsForError(errorMessage, failedArticles))
            {
                helpers.WriteLine($"    {p.SecondaryText.AnsiFg}• {suggestion}{Reset}");
            }

            helpers.WriteLine();
            helpers.WriteLine($"  {p.GetAccentFg().AnsiFg}Enter{Reset}{p.SecondaryText.AnsiFg}:back{Reset}");
            helpers.ClearRemainingLines();

            var command = await ctx.InputHandler.WaitForInputAsync(ct).ConfigureAwait(false);

            if (command.Type == CommandType.TerminalResized)
            {
                options = ctx.GetCurrentRenderOptions();
                continue;
            }

            if (command.Type != CommandType.NoOp)
            {
                break;
            }
        }
    }

    internal static List<string> GetSuggestionsForError(
        string errorMessage,
        IReadOnlyList<ArticleFailure> failedArticles)
    {
        var suggestions = new List<string>();

        if (errorMessage.Contains("No readable articles", StringComparison.OrdinalIgnoreCase))
        {
            suggestions.Add("Open articles in the browser first to populate the page cache");
            suggestions.Add("Some sites block automated content extraction");
            if (failedArticles.Any(f => f.Reason.Contains("bot", StringComparison.OrdinalIgnoreCase) ||
                                        f.Reason.Contains("challenge", StringComparison.OrdinalIgnoreCase)))
            {
                suggestions.Add("Try browsing the blocked articles manually to pass bot checks");
            }
        }
        else if (errorMessage.Contains("FFmpeg", StringComparison.OrdinalIgnoreCase))
        {
            suggestions.Add("Install FFmpeg: brew install ffmpeg (macOS) or apt install ffmpeg (Linux)");
            suggestions.Add("Ensure ffmpeg is available in your PATH");
        }
        else if (errorMessage.Contains("budget", StringComparison.OrdinalIgnoreCase) ||
                 errorMessage.Contains("cost", StringComparison.OrdinalIgnoreCase))
        {
            suggestions.Add("Remove some articles from the collection to reduce cost");
            suggestions.Add("Increase MaxBudgetUsd in configuration");
        }
        else if (errorMessage.Contains("API key", StringComparison.OrdinalIgnoreCase) ||
                 errorMessage.Contains("not configured", StringComparison.OrdinalIgnoreCase) ||
                 errorMessage.Contains("401", StringComparison.Ordinal) ||
                 errorMessage.Contains("403", StringComparison.Ordinal))
        {
            suggestions.Add("Check your OpenAI API key is valid at platform.openai.com/api-keys");
            suggestions.Add("Ensure your account has sufficient credits");
        }
        else if (errorMessage.Contains("TTS", StringComparison.OrdinalIgnoreCase))
        {
            suggestions.Add("Check your OpenAI API key and account status");
            suggestions.Add("Try again — transient API errors are common");
        }

        if (suggestions.Count == 0)
        {
            suggestions.Add("Try again — the error may be transient");
            suggestions.Add("Check the application logs for more details");
        }

        return suggestions;
    }

    internal static void RenderProgressContent(
        RenderHelpers helpers,
        ThemePalette p,
        PodcastProgress? progress,
        int animFrame,
        PodcastCommandHandler.ArticleStatus[] statuses,
        int terminalWidth,
        int terminalHeight)
    {
        var width = Math.Max(20, terminalWidth - 2);
        PodcastCommandHandler.RenderBox(helpers, p, "Generating Podcast", width);
        helpers.WriteLine();

        var phaseName = progress?.Phase switch
        {
            PodcastPhase.CachingContent => "Loading Articles",
            PodcastPhase.GeneratingAudio => "Generating Audio",
            PodcastPhase.AssemblingAudio => "Assembling M4B",
            PodcastPhase.Publishing => "Publishing Feed",
            _ => "Preparing",
        };

        helpers.WriteLine($"  {p.SecondaryText.AnsiFg}Phase:{Reset} {p.PrimaryText.AnsiFg}{phaseName}{Reset}");
        helpers.WriteLine();

        var percent = progress?.PercentComplete ?? 0;
        var barWidth = Math.Max(10, width - 12);
        var fraction = percent / 100.0;
        var isComplete = percent >= 100;
        var filledColor = isComplete ? p.GetSuccessFg().AnsiFg : p.GetWarningFg().AnsiFg;
        var bar = Indicators.RenderEighthBlockBar(filledColor, p.GetMutedFg().AnsiFg, fraction, barWidth);
        helpers.WriteLine($"  {bar} {percent}%");
        helpers.WriteLine();

        var maxArticleLines = Math.Max(1, terminalHeight - helpers.LinesWritten - 4);
        var articleCount = Math.Min(statuses.Length, maxArticleLines);

        var isCaching = progress?.Phase == PodcastPhase.CachingContent;

        for (var i = 0; i < articleCount; i++)
        {
            var status = statuses[i];
            var displayTitle = RenderHelpers.TruncateText(status.Title, width - 10);
            var methodSuffix = status.Method != null
                ? $" {p.SecondaryText.AnsiFg}({status.Method}){Reset}"
                : string.Empty;

            var line = status.State switch
            {
                PodcastCommandHandler.ArticleState.Cached =>
                    $"  {p.GetSuccessFg().AnsiFg}✓{Reset} {displayTitle} {p.SecondaryText.AnsiFg}(cached){Reset}",
                PodcastCommandHandler.ArticleState.Completed =>
                    $"  {p.GetSuccessFg().AnsiFg}✓{Reset} {displayTitle}",
                PodcastCommandHandler.ArticleState.Processing when isCaching =>
                    $"  {p.HeaderTitleFg.AnsiFg}↻{Reset} {displayTitle}" +
                    $"{methodSuffix}" +
                    $"{p.SecondaryText.AnsiFg}{PodcastCommandHandler.AnimationFrames[animFrame]}{Reset}",
                PodcastCommandHandler.ArticleState.Processing =>
                    $"  {p.HeaderTitleFg.AnsiFg}♫{Reset} {displayTitle}" +
                    $"{p.SecondaryText.AnsiFg}{PodcastCommandHandler.AnimationFrames[animFrame]}{Reset}",
                PodcastCommandHandler.ArticleState.Failed =>
                    $"  {p.ErrorFg.AnsiFg}✗{Reset} {displayTitle}",
                _ =>
                    $"  {p.SecondaryText.AnsiFg}  {displayTitle}{Reset}",
            };

            helpers.WriteLine(line);
        }

        if (statuses.Length > articleCount)
        {
            helpers.WriteLine(
                $"  {p.SecondaryText.AnsiFg}... and {statuses.Length - articleCount} more{Reset}");
        }

        helpers.WriteLine();
        helpers.WriteLine($"  {p.GetAccentFg().AnsiFg}Esc{Reset}{p.SecondaryText.AnsiFg}:cancel{Reset}");
    }

    internal static void CopyToClipboardOsc52(string text)
    {
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
        Console.Write($"\x1b]52;c;{base64}\x07");
    }

    internal static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
        {
            return $"{(int)duration.TotalHours}h {duration.Minutes}m";
        }

        return $"{(int)duration.TotalMinutes}m {duration.Seconds}s";
    }

    internal static string FormatFileSize(long bytes)
    {
        return bytes switch
        {
            >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:F1} GB",
            >= 1_048_576 => $"{bytes / 1_048_576.0:F1} MB",
            >= 1024 => $"{bytes / 1024.0:F1} KB",
            _ => $"{bytes} B",
        };
    }
}
