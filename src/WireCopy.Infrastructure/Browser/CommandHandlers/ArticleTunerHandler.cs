// Licensed under the MIT License. See LICENSE in the repository root.

using HtmlAgilityPack;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Infrastructure.Browser.Themes;
using WireCopy.Infrastructure.Browser.UI.Components;

namespace WireCopy.Infrastructure.Browser.CommandHandlers;

/// <summary>
/// Interactive article-layout tuner (workspace-8qyo): 'L' in reader view walks
/// HEADLINE → BODY → IGNORE in three steps. For each step, AI (when configured)
/// and DOM heuristics propose XPath candidates; the sidecar lens highlights every
/// match live; j/k cycles, Enter confirms (Space toggles ignores), Esc backs out.
/// The confirmed selectors persist as the per-domain 'tuned' PageTypeEntry that
/// <see cref="ISelectorBasedArticleExtractor"/> prefers on every future visit.
/// Excluded regions are REMOVED before paragraph collection, so an inline promo
/// can never truncate an article.
/// </summary>
internal static class ArticleTunerHandler
{
    private const string EntryName = "tuned";

    private static readonly string[] HeadlineProbes =
    [
        "//article//h1",
        "//h1",
        "//*[@itemprop='headline']",
        "//header//h1",
        "//*[contains(@class,'headline')]",
    ];

    private static readonly string[] BodyProbes =
    [
        "//*[@itemprop='articleBody']",
        "//article",
        "//*[@role='main']",
        "//main",
        "//*[contains(@class,'article-body')]",
        "//*[contains(@class,'story-body')]",
        "//*[contains(@class,'post-content')]",
        "//*[contains(@class,'entry-content')]",
    ];

    private static readonly string[] IgnoreProbes =
    [
        "//aside",
        "//*[contains(@class,'related')]",
        "//*[contains(@class,'newsletter')]",
        "//*[contains(@class,'promo')]",
        "//*[contains(@class,'share')]",
        "//*[contains(@class,'comment')]",
        "//*[contains(@class,'recirc')]",
        "//*[contains(@class,'ad-')]",
    ];

    private enum TunerKey
    {
        None,
        Next,
        Prev,
        Toggle,
        Confirm,
        Cancel,
    }

    public static async Task HandleTuneAsync(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        var navContext = ctx.NavigationService.CurrentContext;
        var page = navContext.CurrentPage;
        if (navContext.ViewMode != ViewMode.Readable || page == null || string.IsNullOrEmpty(page.RawHtml))
        {
            ctx.NavigationService.SetStatusMessage("Layout tuning works in reader view (open an article first)");
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
            return;
        }

        var doc = new HtmlDocument();
        doc.LoadHtml(page.RawHtml);

        using var scope = ctx.ScopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetService<IArticleLayoutStore>();
        var selectorExtractor = scope.ServiceProvider.GetService<ISelectorBasedArticleExtractor>();
        var aiExtractor = scope.ServiceProvider.GetService<IAiArticleExtractor>();
        var session = scope.ServiceProvider.GetService<IBrowserSession>();
        if (store == null || selectorExtractor == null)
        {
            ctx.NavigationService.SetStatusMessage("Layout store unavailable");
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
            return;
        }

        // AI seed (best-effort): its selectors become candidate #1 of each step.
        ArticleSelectors? aiSeed = null;
        if (aiExtractor is { IsConfigured: true })
        {
            // workspace-wef6.5: the long AnalyzeAsync runs in the animated
            // activity slot instead of a 2-minute status message.
            ctx.NavigationService.SetActivity("ai", "✨ asking AI for layout candidates…", priority: 1);
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
            try
            {
                var proposal = await aiExtractor.AnalyzeAsync(page.Url, page.RawHtml, ct).ConfigureAwait(false);
                aiSeed = proposal?.PageTypes.Count > 0 ? proposal.PageTypes[0].Selectors : null;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                ctx.Logger.LogWarning(ex, "AI layout proposal failed; falling back to heuristics");
            }
            finally
            {
                ctx.NavigationService.ClearActivity("ai");
            }
        }

        var headlineCands = BuildCandidates(doc, aiSeed?.Headline, HeadlineProbes, minMatches: 1, maxMatches: 5);
        var bodyCands = BuildBodyCandidates(doc, aiSeed?.Body);
        var ignoreCands = BuildCandidates(doc, aiSeed?.ExcludeRegions, IgnoreProbes, minMatches: 1, maxMatches: 200);

        if (headlineCands.Count == 0 || bodyCands.Count == 0)
        {
            ctx.NavigationService.SetStatusMessage("Couldn't find layout candidates on this page");
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
            return;
        }

        var palette = BuiltInThemes.Get(ctx.ThemeProvider.CurrentTheme);
        var overlay = new SetupWizardOverlay.State { Mode = SetupWizardOverlay.Mode.Card };
        ctx.SetOverlayPainter(opts => SetupWizardOverlay.Render(overlay, palette, opts.TerminalWidth, opts.TerminalHeight));

        try
        {
            ctx.Logger.LogInformation(
                "Tuner: candidates headline={H} body={B} ignore={I}",
                headlineCands.Count,
                bodyCands.Count,
                ignoreCands.Count);
            var headline = await RunSingleStepAsync(
                ctx,
                overlay,
                session,
                options,
                "Tune layout — 1/3 Headline",
                "Which element is the headline?",
                headlineCands,
                ct).ConfigureAwait(false);
            if (headline == null)
            {
                await CancelAsync(ctx, session).ConfigureAwait(false);
                return;
            }

            var body = await RunSingleStepAsync(
                ctx,
                overlay,
                session,
                options,
                "Tune layout — 2/3 Body",
                "Which container holds the article text?",
                bodyCands,
                ct).ConfigureAwait(false);
            if (body == null)
            {
                await CancelAsync(ctx, session).ConfigureAwait(false);
                return;
            }

            var ignores = await RunMultiStepAsync(
                ctx,
                overlay,
                session,
                options,
                "Tune layout — 3/3 Ignore",
                "Mark visual junk to ignore (never truncates the article)",
                ignoreCands,
                ct).ConfigureAwait(false);
            if (ignores == null)
            {
                await CancelAsync(ctx, session).ConfigureAwait(false);
                return;
            }

            await PersistAsync(ctx, store, selectorExtractor, page.Url, page.RawHtml, headline, body, ignores)
                .ConfigureAwait(false);
        }
        finally
        {
            ctx.SetOverlayPainter(null);
            await ClearLensHighlightAsync(session).ConfigureAwait(false);
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Candidates = AI seed first, then probes that actually match the page.</summary>
    internal static List<(string XPath, int Matches)> BuildCandidates(
        HtmlDocument doc, IReadOnlyList<string>? aiSeed, string[] probes, int minMatches, int maxMatches)
    {
        var result = new List<(string, int)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var xpath in (aiSeed ?? Enumerable.Empty<string>()).Concat(probes))
        {
            if (string.IsNullOrWhiteSpace(xpath) || !seen.Add(xpath))
            {
                continue;
            }

            var count = CountMatches(doc, xpath);
            if (count >= minMatches && count <= maxMatches)
            {
                result.Add((xpath, count));
            }
        }

        return result;
    }

    /// <summary>Body candidates ranked by paragraph density (most article-like first).</summary>
    internal static List<(string XPath, int Matches)> BuildBodyCandidates(
        HtmlDocument doc, IReadOnlyList<string>? aiSeed)
    {
        var ranked = new List<(string XPath, int Matches, int Paragraphs)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var xpath in (aiSeed ?? Enumerable.Empty<string>()).Concat(BodyProbes))
        {
            if (string.IsNullOrWhiteSpace(xpath) || !seen.Add(xpath))
            {
                continue;
            }

            HtmlNodeCollection? nodes;
            try
            {
                nodes = doc.DocumentNode.SelectNodes(xpath);
            }
            catch (System.Xml.XPath.XPathException)
            {
                continue;
            }

            if (nodes is not { Count: > 0 })
            {
                continue;
            }

            var paragraphs = nodes.Sum(n => n.SelectNodes(".//p")?.Count ?? 0);
            if (paragraphs >= 3)
            {
                ranked.Add((xpath, nodes.Count, paragraphs));
            }
        }

        // AI seed (index 0 in insertion order) keeps its slot; heuristics rank by density.
        return ranked
            .OrderByDescending(r => aiSeed != null && aiSeed.Contains(r.XPath) ? int.MaxValue : r.Paragraphs)
            .Select(r => (r.XPath, r.Matches))
            .ToList();
    }

    private static int CountMatches(HtmlDocument doc, string xpath)
    {
        try
        {
            return doc.DocumentNode.SelectNodes(xpath)?.Count ?? 0;
        }
        catch (System.Xml.XPath.XPathException)
        {
            return -1;
        }
    }

    private static async Task<string?> RunSingleStepAsync(
        CommandContext ctx,
        SetupWizardOverlay.State overlay,
        IBrowserSession? session,
        RenderOptions options,
        string title,
        string prompt,
        List<(string XPath, int Matches)> candidates,
        CancellationToken ct)
    {
        var cursor = 0;
        while (!ct.IsCancellationRequested)
        {
            overlay.Card = BuildCard(
                title,
                prompt,
                candidates,
                cursor,
                null,
                "j/k: next candidate · Enter: confirm · Esc: cancel");
            await HighlightAsync(session, candidates[cursor].XPath).ConfigureAwait(false);
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);

            var command = await ctx.InputHandler.WaitForInputAsync(ct).ConfigureAwait(false);
            switch (Classify(command))
            {
                case TunerKey.Next:
                    cursor = (cursor + 1) % candidates.Count;
                    break;
                case TunerKey.Prev:
                    cursor = (cursor - 1 + candidates.Count) % candidates.Count;
                    break;
                case TunerKey.Confirm:
                    return candidates[cursor].XPath;
                case TunerKey.Cancel:
                    return null;
            }
        }

        return null;
    }

    private static async Task<List<string>?> RunMultiStepAsync(
        CommandContext ctx,
        SetupWizardOverlay.State overlay,
        IBrowserSession? session,
        RenderOptions options,
        string title,
        string prompt,
        List<(string XPath, int Matches)> candidates,
        CancellationToken ct)
    {
        if (candidates.Count == 0)
        {
            return [];
        }

        var cursor = 0;
        var selected = new HashSet<int>();
        while (!ct.IsCancellationRequested)
        {
            overlay.Card = BuildCard(
                title,
                prompt,
                candidates,
                cursor,
                selected,
                "j/k: next · Space: mark · Enter: done · Esc: cancel");
            await HighlightAsync(session, candidates[cursor].XPath).ConfigureAwait(false);
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);

            var command = await ctx.InputHandler.WaitForInputAsync(ct).ConfigureAwait(false);
            switch (Classify(command))
            {
                case TunerKey.Next:
                    cursor = (cursor + 1) % candidates.Count;
                    break;
                case TunerKey.Prev:
                    cursor = (cursor - 1 + candidates.Count) % candidates.Count;
                    break;
                case TunerKey.Toggle:
                    if (!selected.Remove(cursor))
                    {
                        selected.Add(cursor);
                    }

                    break;
                case TunerKey.Confirm:
                    return selected.Select(i => candidates[i].XPath).ToList();
                case TunerKey.Cancel:
                    return null;
            }
        }

        return null;
    }

    private static SetupWizardOverlay.WizardCard BuildCard(
        string title,
        string prompt,
        List<(string XPath, int Matches)> candidates,
        int cursor,
        HashSet<int>? selected,
        string hint)
    {
        var card = new SetupWizardOverlay.WizardCard
        {
            Title = title,
            Prompt = prompt,
            Cursor = cursor,
            Hint = hint,
            Footnote = "highlighted live in the sidecar",
        };
        for (var i = 0; i < candidates.Count; i++)
        {
            string mark;
            if (selected == null)
            {
                mark = string.Empty;
            }
            else
            {
                mark = selected.Contains(i) ? "[x] " : "[ ] ";
            }

            card.Options.Add(new SetupWizardOverlay.CardOption
            {
                Label = $"{mark}{candidates[i].XPath} ({candidates[i].Matches} match{(candidates[i].Matches == 1 ? string.Empty : "es")})",
                Identifier = candidates[i].XPath,
            });
        }

        return card;
    }

    private static TunerKey Classify(NavigationCommand command)
    {
        if (command.RawKeyChar is 'j')
        {
            return TunerKey.Next;
        }

        if (command.RawKeyChar is 'k')
        {
            return TunerKey.Prev;
        }

        if (command.RawKeyChar is ' ')
        {
            return TunerKey.Toggle;
        }

        return command.Type switch
        {
            CommandType.MoveDown => TunerKey.Next,
            CommandType.MoveUp => TunerKey.Prev,
            CommandType.ToggleSelection => TunerKey.Toggle,
            CommandType.ActivateLink => TunerKey.Confirm,
            CommandType.GoBack or CommandType.Quit => TunerKey.Cancel,
            _ => TunerKey.None,
        };
    }

    private static async Task PersistAsync(
        CommandContext ctx,
        IArticleLayoutStore store,
        ISelectorBasedArticleExtractor selectorExtractor,
        string url,
        string html,
        string headline,
        string body,
        List<string> ignores)
    {
        var domain = ArticleLayoutDomains.FromUrl(url);
        if (domain is null)
        {
            ctx.NavigationService.ShowToast(ToastType.Error, "Layout NOT saved", "this page has no usable domain");
            return;
        }

        var entry = new PageTypeEntry
        {
            Name = EntryName,
            Priority = 80,
            Selectors = new ArticleSelectors
            {
                Headline = [headline],
                Body = [body],
                ExcludeRegions = ignores,
            },
            Provenance = new ProvenanceInfo { Model = "manual", SampleUrl = url },
        };
        var candidate = new ArticleSelectorConfig { Domain = domain, PageTypes = [entry] };

        ctx.Logger.LogInformation(
            "Tuner: confirmed headline={Headline} body={Body} ignores={Ignores} domain={Domain}",
            headline,
            body,
            string.Join("|", ignores),
            domain);

        // Self-test before persisting — exactly like the AI regenerate path.
        var trial = selectorExtractor.Extract(candidate, url, html);
        if (trial == null)
        {
            ctx.Logger.LogWarning("Tuner: self-test extraction returned null — not saving");
            ctx.NavigationService.ShowToast(ToastType.Error, "Layout NOT saved", "those selectors extract nothing here");
            return;
        }

        var existing = await store.LoadAsync(domain).ConfigureAwait(false);
        var merged = ArticleLayoutCommandHandler.MergeByName(existing, candidate);
        await store.SaveAsync(merged).ConfigureAwait(false);
        ctx.Logger.LogInformation("Tuner: layout saved for {Domain} ({Entries} entries)", merged.Domain, merged.PageTypes.Count);

        // Toast, not status: the reader-view status bar's progress rule swallows
        // transient messages (pre-existing; tracked separately), toasts render
        // on every view.
        ctx.NavigationService.ShowToast(ToastType.Success, $"Layout tuned for {domain}", "future visits use it");
    }

    private static async Task CancelAsync(CommandContext ctx, IBrowserSession? session)
    {
        ctx.NavigationService.SetStatusMessage("Layout tuning cancelled");
        await ClearLensHighlightAsync(session).ConfigureAwait(false);
    }

    private static async Task HighlightAsync(IBrowserSession? session, string xpath)
    {
        if (session == null)
        {
            return;
        }

        try
        {
            var lens = await BrowserDockCommandHandler.GetLensOrDisplayPageAsync(session).ConfigureAwait(false);
            if (lens != null)
            {
                await lens.EvaluateAsync<int>(TunerScript.Highlight, new { selector = xpath, dialect = "xpath" })
                    .ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // Highlighting is advisory; the card still shows match counts.
        }
    }

    private static async Task ClearLensHighlightAsync(IBrowserSession? session)
    {
        if (session == null)
        {
            return;
        }

        try
        {
            var lens = await BrowserDockCommandHandler.GetLensOrDisplayPageAsync(session).ConfigureAwait(false);
            if (lens != null)
            {
                await lens.EvaluateAsync<string>(TunerScript.Clear).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // Cosmetic.
        }
    }
}
