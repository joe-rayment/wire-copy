// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;

namespace WireCopy.Infrastructure.Browser.CommandHandlers;

/// <summary>
/// Per-domain scraping strategy chooser. Replaces the old visual-variant
/// "alternate layouts" cycling. Lists Document Order / AI Curated / RSS Feed,
/// previews each, and persists the chosen strategy via
/// <see cref="IHierarchyConfigStore"/>.
/// </summary>
internal static class StrategyChooserHandler
{
    /// <summary>
    /// Per-strategy availability probe budget (workspace-in59). Previously a
    /// slow RSS probe could stall the whole chooser for ~3 minutes on
    /// macleans.ca and similar sites without an advertised feed. With a
    /// 5-second cap per strategy, the worst case is bounded at
    /// <c>5s × strategy count</c> (~15s for 3 strategies).
    /// </summary>
    private static readonly TimeSpan StrategyProbeBudget = TimeSpan.FromSeconds(5);

    private static readonly string[] Spinner =
    {
        "⠋", "⠙", "⠹", "⠸",
        "⠼", "⠴", "⠦", "⠧",
        "⠇", "⠏",
    };

    /// <summary>
    /// Opens the scraping-strategy chooser for the current page.
    /// Builds a LayoutCandidate per available strategy, enters preview mode.
    /// Document Order is always first; AI Curated and RSS appear when available.
    /// </summary>
    public static async Task HandleOpenChooserAsync(
        CommandContext ctx,
        RenderOptions options,
        CancellationToken ct)
    {
        var navContext = ctx.NavigationService.CurrentContext;
        if (navContext.ViewMode != ViewMode.Hierarchical || navContext.CurrentPage == null)
        {
            return;
        }

        if (ctx.NavigationService.IsInPreviewMode)
        {
            ctx.NavigationService.CancelPreview();
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
            return;
        }

        ctx.NavigationService.SetStatusMessage("Loading scraping strategies…", TimeSpan.FromMinutes(1));
        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);

        try
        {
            using var scope = ctx.ScopeFactory.CreateScope();
            var registry = scope.ServiceProvider.GetService<IScrapingStrategyRegistry>();
            if (registry == null || registry.All.Count == 0)
            {
                ctx.NavigationService.SetStatusMessage("Strategy chooser unavailable");
                await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
                return;
            }

            var page = navContext.CurrentPage;
            var html = page.RawHtml ?? string.Empty;
            var buildCache = ctx.PageCache.TryGetBuildCache(page.Url);
            var links = (IReadOnlyList<LinkInfo>)(buildCache?.Links ?? new List<LinkInfo>());

            byte[]? screenshot = null;
            if (scope.ServiceProvider.GetService<IBrowserSessionControl>() is IBrowserSession session)
            {
                try
                {
                    screenshot = await session.CaptureScreenshotAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    ctx.Logger.LogDebug(ex, "Strategy chooser: screenshot capture failed");
                }
            }

            var configStore = scope.ServiceProvider.GetService<IHierarchyConfigStore>();
            var savedConfig = configStore != null
                ? await configStore.GetConfigAsync(page.Url).ConfigureAwait(false)
                : null;

            var stContext = new ScrapingStrategyContext
            {
                PageUrl = page.Url,
                Html = html,
                Links = links,
                Screenshot = screenshot,
                SavedConfig = savedConfig,
            };

            var candidates = new List<LayoutCandidate>();
            var unavailable = new List<(string Name, string? Reason)>();
            int spinnerFrame = 0;

            foreach (var strategy in registry.All)
            {
                ctx.NavigationService.SetStatusMessage(
                    $"{Spinner[spinnerFrame++ % Spinner.Length]} Probing {strategy.DisplayName}…",
                    TimeSpan.FromMinutes(1));
                await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);

                // workspace-in59: cap each strategy's availability probe at
                // StrategyProbeBudget so a single slow probe (the
                // ProbeWellKnownFeedsAsync hang on RSS-less domains) can't stall
                // the entire chooser. A timed-out probe surfaces as "unavailable
                // (probe timed out)" so the user still sees the strategy listed.
                ScrapingStrategyAvailability availability;
                try
                {
                    using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    probeCts.CancelAfter(StrategyProbeBudget);
                    availability = await strategy.IsAvailableAsync(stContext, probeCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    ctx.Logger.LogWarning(
                        "Strategy probe for {Id} exceeded {Budget}s budget",
                        strategy.Id,
                        StrategyProbeBudget.TotalSeconds);
                    availability = new ScrapingStrategyAvailability
                    {
                        IsAvailable = false,
                        ReasonWhenUnavailable = $"probe timed out after {StrategyProbeBudget.TotalSeconds:0}s",
                    };
                }

                if (!availability.IsAvailable)
                {
                    unavailable.Add((strategy.DisplayName, availability.ReasonWhenUnavailable));

                    // workspace-33jw: surface unavailable strategies in the chooser as
                    // disabled rows so the user can DISCOVER them. Previously we silently
                    // dropped them, leaving e.g. AI Curated invisible to anyone without
                    // an OpenAI key. Cycling onto an unavailable row reverts the
                    // preview to the page's existing tree (so the screen doesn't go
                    // blank); applying it shows a hint rather than saving.
                    var currentTreeForStub = page.LinkTree;
                    if (currentTreeForStub != null)
                    {
                        var disabledStub = new SiteHierarchyConfig
                        {
                            Domain = ExtractDomain(page.Url),
                            UrlPattern = ScrapingStrategies.DocumentOrderStrategy.BuildUrlPattern(page.Url),
                            Sections = new List<HierarchySection>(),
                            CreatedAt = DateTime.UtcNow,
                            ModelVersion = "unavailable",
                            Kind = LayoutKind.DocumentOrder,
                            Version = 2,
                            Strategy = strategy.Id,
                        };
                        candidates.Add(new LayoutCandidate
                        {
                            Config = disabledStub,
                            Summary = $"✗ {strategy.DisplayName} · {availability.ReasonWhenUnavailable ?? "unavailable"}",
                            PreviewTree = currentTreeForStub,
                            IsUnavailable = true,
                            UnavailableReason = availability.ReasonWhenUnavailable,
                        });
                    }

                    continue;
                }

                // Document order is fast — always preview live.
                // AI Curated only previews if cached; otherwise selecting it runs the analyzer.
                // RSS previews live (parse-feed cost is acceptable).
                bool canPreviewNow = strategy.Id != ScrapingStrategies.AiCuratedStrategy.StrategyId
                    || (savedConfig?.AiResult != null);

                if (!canPreviewNow)
                {
                    // Add a "stub" candidate using current tree as preview so the
                    // user can still pick it — selection runs the analyzer.
                    var currentTree = page.LinkTree;
                    if (currentTree == null)
                    {
                        continue;
                    }

                    var stubConfig = new SiteHierarchyConfig
                    {
                        Domain = ExtractDomain(page.Url),
                        UrlPattern = ScrapingStrategies.DocumentOrderStrategy.BuildUrlPattern(page.Url),
                        Sections = new List<HierarchySection>(),
                        CreatedAt = DateTime.UtcNow,
                        ModelVersion = "ai-curated-pending",
                        Kind = LayoutKind.AiCurated,
                        Version = 2,
                        Strategy = strategy.Id,
                    };

                    candidates.Add(new LayoutCandidate
                    {
                        Config = stubConfig,
                        Summary = $"{strategy.DisplayName} · {availability.StatusDetail ?? strategy.Description}",
                        PreviewTree = currentTree,
                    });
                    continue;
                }

                try
                {
                    var result = await strategy.BuildTreeAsync(stContext, ct).ConfigureAwait(false);
                    candidates.Add(new LayoutCandidate
                    {
                        Config = result.Config,
                        Summary = result.Summary
                            ?? $"{strategy.DisplayName} · {availability.StatusDetail ?? strategy.Description}",
                        PreviewTree = result.Tree,
                    });
                }
                catch (Exception ex)
                {
                    ctx.Logger.LogWarning(ex, "Strategy {Id} failed to build tree", strategy.Id);
                    unavailable.Add((strategy.DisplayName, ex.Message));
                }
            }

            if (candidates.Count == 0)
            {
                var why = unavailable.Count > 0
                    ? string.Join("; ", unavailable.Select(u => $"{u.Name}: {u.Reason}"))
                    : "no strategies available";
                ctx.NavigationService.SetStatusMessage($"Scraping strategies: {why}");
                await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
                return;
            }

            ctx.NavigationService.SetStatusMessage(
                $"✨ {candidates.Count} strategies ready · ◀/▶ to preview, Enter to apply, Esc to cancel");
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);

            ctx.NavigationService.EnterPreviewMode(candidates);
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // user cancelled; clean exit
        }
        catch (Exception ex)
        {
            ctx.Logger.LogError(ex, "Strategy chooser failed");
            ctx.NavigationService.SetStatusMessage("Strategy chooser failed");
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Apply the selected strategy preview, run the AI analyzer if the
    /// candidate was a stub (AI Curated, no cached result yet), and
    /// persist the resulting config.
    /// </summary>
    public static async Task HandleApplyAsync(
        CommandContext ctx,
        RenderOptions options,
        CancellationToken ct)
    {
        var selected = ctx.NavigationService.ApplyPreview();
        if (selected == null)
        {
            return;
        }

        // workspace-33jw: refuse to save unavailable strategies. The user pressed
        // Enter on a disabled row (e.g., AI Curated without an API key) — show a
        // hint instead of mutating the saved config.
        if (selected.IsUnavailable)
        {
            var hint = selected.UnavailableReason ?? "strategy unavailable";
            ctx.NavigationService.SetStatusMessage($"Cannot apply · {hint}");
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
            return;
        }

        try
        {
            using var scope = ctx.ScopeFactory.CreateScope();
            var configStore = scope.ServiceProvider.GetService<IHierarchyConfigStore>();
            var registry = scope.ServiceProvider.GetService<IScrapingStrategyRegistry>();

            var configToSave = selected.Config;

            // If the user selected an AI-Curated stub, run the analyzer now.
            if (registry != null
                && configToSave.Strategy == ScrapingStrategies.AiCuratedStrategy.StrategyId
                && configToSave.AiResult == null)
            {
                ctx.NavigationService.SetStatusMessage("Running AI curation…", TimeSpan.FromMinutes(2));
                await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);

                var page = ctx.NavigationService.CurrentContext.CurrentPage;
                if (page != null)
                {
                    var buildCache = ctx.PageCache.TryGetBuildCache(page.Url);
                    var links = (IReadOnlyList<LinkInfo>)(buildCache?.Links ?? new List<LinkInfo>());
                    byte[]? screenshot = null;
                    if (scope.ServiceProvider.GetService<IBrowserSessionControl>() is IBrowserSession session)
                    {
                        try
                        {
                            screenshot = await session.CaptureScreenshotAsync().ConfigureAwait(false);
                        }
                        catch
                        {
                            // best-effort
                        }
                    }

                    var strategy = registry.GetById(ScrapingStrategies.AiCuratedStrategy.StrategyId);
                    if (strategy != null)
                    {
                        var stContext = new ScrapingStrategyContext
                        {
                            PageUrl = page.Url,
                            Html = page.RawHtml ?? string.Empty,
                            Links = links,
                            Screenshot = screenshot,
                            SavedConfig = configToSave,
                        };

                        var result = await strategy.BuildTreeAsync(stContext, ct).ConfigureAwait(false);
                        configToSave = result.Config;
                        page.SetLinkTree(result.Tree);
                    }
                }
            }

            if (configStore != null)
            {
                await configStore.SaveConfigAsync(configToSave).ConfigureAwait(false);
                ctx.NavigationService.SetStatusMessage($"✔ Strategy saved · {selected.Summary}");
            }
            else
            {
                ctx.NavigationService.SetStatusMessage($"✔ Applied · {selected.Summary}");
            }
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Failed to apply scraping strategy");
            ctx.NavigationService.SetStatusMessage($"Apply failed · {ex.Message}");
        }

        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
    }

    private static string ExtractDomain(string url)
    {
        try
        {
            return new Uri(url).Host.ToLowerInvariant();
        }
        catch
        {
            return "unknown";
        }
    }
}
