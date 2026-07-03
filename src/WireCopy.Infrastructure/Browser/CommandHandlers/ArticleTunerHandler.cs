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
/// match live; j/k (or arrows) cycles, Enter confirms (Space toggles ignores),
/// 'h' steps back to the previous step keeping its selection (workspace-3uzl.4),
/// Esc backs out. The confirmed selectors persist as the per-domain 'tuned'
/// PageTypeEntry that <see cref="ISelectorBasedArticleExtractor"/> prefers on
/// every future visit. Excluded regions are REMOVED before paragraph collection,
/// so an inline promo can never truncate an article.
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

        /// <summary>workspace-3uzl.4: 'h' — back to the previous step, keeping its selection.</summary>
        Back,
        Cancel,
    }

    /// <summary>workspace-3uzl.4: how a step ended — the loop in HandleTuneAsync routes on this.</summary>
    private enum StepOutcome
    {
        Confirmed,
        Back,
        Cancelled,
    }

    /// <summary>workspace-3uzl.4: how persisting ended — a failed self-test returns to step 3, not teardown.</summary>
    private enum PersistOutcome
    {
        Saved,

        /// <summary>The trial extraction returned nothing; the user should pick different candidates.</summary>
        SelfTestFailed,

        /// <summary>Unrecoverable (no usable domain) — retrying the same page cannot help.</summary>
        Fatal,
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
        var aiFailed = false;
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
                aiFailed = true;
            }
            finally
            {
                ctx.NavigationService.ClearActivity("ai");
            }

            // workspace-3uzl.1: a failed AI proposal used to degrade silently —
            // the user saw fewer candidates with no explanation. Toasts render
            // on every view, so this survives the overlay opening next.
            if (aiFailed)
            {
                ctx.NavigationService.ShowToast(
                    ToastType.Info, "AI unavailable", "showing heuristic candidates only");
            }
        }

        var headlineCands = BuildCandidates(doc, aiSeed?.Headline, HeadlineProbes, minMatches: 1, maxMatches: 5);
        var bodyCands = BuildBodyCandidates(doc, aiSeed?.Body);
        var ignoreCands = BuildCandidates(doc, aiSeed?.ExcludeRegions, IgnoreProbes, minMatches: 1, maxMatches: 200);

        if (headlineCands.Count == 0 || bodyCands.Count == 0)
        {
            // workspace-3uzl.5: name a next step instead of a bare shrug, and
            // say when the AI seed was expected but missing.
            var aiNote = aiExtractor is { IsConfigured: true } && aiSeed == null
                ? " (the AI proposal was unavailable too)"
                : string.Empty;
            ctx.NavigationService.SetStatusMessage(
                $"No layout candidates found{aiNote} — this page may have an unusual structure; try a different article");
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
            return;
        }

        // workspace-3uzl.2: resolve lens availability ONCE so every card's
        // footnote tells the truth — "highlighted live" only when a lens page
        // actually exists.
        var lensAvailable = await LensAvailableAsync(session).ConfigureAwait(false);

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

            // workspace-3uzl.4: the three steps run as a state machine so 'h'
            // (Back) can return to an earlier step — and a failed persist
            // self-test returns to step 3 — with every step's cursor/marks
            // preserved instead of tearing the tuner down.
            string? headline = null;
            string? body = null;
            var headlineCursor = 0;
            var bodyCursor = 0;
            var ignoreCursor = 0;
            var ignoreSelected = new HashSet<int>();
            var step = 0;
            while (!ct.IsCancellationRequested)
            {
                switch (step)
                {
                    case 0:
                    {
                        var (outcome, choice, cursor) = await RunSingleStepAsync(
                            ctx,
                            overlay,
                            session,
                            options,
                            "Tune layout — 1/3 Headline",
                            "Which element is the headline?",
                            headlineCands,
                            headlineCursor,
                            allowBack: false,
                            lensAvailable,
                            ct).ConfigureAwait(false);
                        headlineCursor = cursor;
                        if (outcome != StepOutcome.Confirmed)
                        {
                            await CancelAsync(ctx, session).ConfigureAwait(false);
                            return;
                        }

                        headline = choice;
                        step = 1;
                        break;
                    }

                    case 1:
                    {
                        var (outcome, choice, cursor) = await RunSingleStepAsync(
                            ctx,
                            overlay,
                            session,
                            options,
                            "Tune layout — 2/3 Body",
                            "Which container holds the article text?",
                            bodyCands,
                            bodyCursor,
                            allowBack: true,
                            lensAvailable,
                            ct).ConfigureAwait(false);
                        bodyCursor = cursor;
                        if (outcome == StepOutcome.Cancelled)
                        {
                            await CancelAsync(ctx, session).ConfigureAwait(false);
                            return;
                        }

                        if (outcome == StepOutcome.Back)
                        {
                            step = 0;
                            break;
                        }

                        body = choice;
                        step = 2;
                        break;
                    }

                    default:
                    {
                        var (outcome, ignores, cursor) = await RunMultiStepAsync(
                            ctx,
                            overlay,
                            session,
                            options,
                            "Tune layout — 3/3 Ignore",
                            "Mark visual junk to ignore (never truncates the article)",
                            ignoreCands,
                            ignoreCursor,
                            ignoreSelected,
                            lensAvailable,
                            ct).ConfigureAwait(false);
                        ignoreCursor = cursor;
                        if (outcome == StepOutcome.Cancelled)
                        {
                            await CancelAsync(ctx, session).ConfigureAwait(false);
                            return;
                        }

                        if (outcome == StepOutcome.Back)
                        {
                            step = 1;
                            break;
                        }

                        var persisted = await PersistAsync(
                            ctx, store, selectorExtractor, page.Url, page.RawHtml, headline!, body!, ignores!)
                            .ConfigureAwait(false);
                        if (persisted == PersistOutcome.SelfTestFailed)
                        {
                            // Keep the overlay alive: back to the ignore step
                            // (or body when there are no ignore candidates to
                            // re-pick) so the user can try different selectors.
                            step = ignoreCands.Count > 0 ? 2 : 1;
                            break;
                        }

                        return;
                    }
                }
            }
        }
        finally
        {
            ctx.SetOverlayPainter(null);
            await ClearLensHighlightAsync(session).ConfigureAwait(false);
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Candidates = AI seed first, then probes that actually match the page.
    /// workspace-3uzl.7: each candidate carries whether it came from the AI
    /// seed so the card can label its origin.
    /// </summary>
    internal static List<(string XPath, int Matches, bool IsAiSeed)> BuildCandidates(
        HtmlDocument doc, IReadOnlyList<string>? aiSeed, string[] probes, int minMatches, int maxMatches)
    {
        var result = new List<(string, int, bool)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (xpath, isAiSeed) in (aiSeed ?? Enumerable.Empty<string>()).Select(x => (x, true))
                     .Concat(probes.Select(x => (x, false))))
        {
            if (string.IsNullOrWhiteSpace(xpath) || !seen.Add(xpath))
            {
                continue;
            }

            var count = CountMatches(doc, xpath);
            if (count >= minMatches && count <= maxMatches)
            {
                result.Add((xpath, count, isAiSeed));
            }
        }

        return result;
    }

    /// <summary>Body candidates ranked by paragraph density (most article-like first).</summary>
    internal static List<(string XPath, int Matches, bool IsAiSeed)> BuildBodyCandidates(
        HtmlDocument doc, IReadOnlyList<string>? aiSeed)
    {
        var ranked = new List<(string XPath, int Matches, bool IsAiSeed, int Paragraphs)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (xpath, isAiSeed) in (aiSeed ?? Enumerable.Empty<string>()).Select(x => (x, true))
                     .Concat(BodyProbes.Select(x => (x, false))))
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
                ranked.Add((xpath, nodes.Count, isAiSeed, paragraphs));
            }
        }

        // AI seed keeps its lead slot; heuristics rank by density.
        return ranked
            .OrderByDescending(r => r.IsAiSeed ? int.MaxValue : r.Paragraphs)
            .Select(r => (r.XPath, r.Matches, r.IsAiSeed))
            .ToList();
    }

    /// <summary>
    /// workspace-3uzl.2/.7: the footnote states plainly whether the sidecar lens
    /// is live, and AI-seeded candidates are labelled " (AI)".
    /// </summary>
    internal static SetupWizardOverlay.WizardCard BuildCard(
        string title,
        string prompt,
        List<(string XPath, int Matches, bool IsAiSeed)> candidates,
        int cursor,
        HashSet<int>? selected,
        string hint,
        bool lensAvailable)
    {
        var card = new SetupWizardOverlay.WizardCard
        {
            Title = title,
            Prompt = prompt,
            Cursor = cursor,
            Hint = hint,
            Footnote = lensAvailable
                ? "highlighted live in the sidecar"
                : "sidecar not docked — showing match counts only",
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

            var aiTag = candidates[i].IsAiSeed ? " (AI)" : string.Empty;
            card.Options.Add(new SetupWizardOverlay.CardOption
            {
                Label = $"{mark}{candidates[i].XPath} ({candidates[i].Matches} match{(candidates[i].Matches == 1 ? string.Empty : "es")}){aiTag}",
                Identifier = candidates[i].XPath,
            });
        }

        return card;
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

    private static async Task<(StepOutcome Outcome, string? Choice, int Cursor)> RunSingleStepAsync(
        CommandContext ctx,
        SetupWizardOverlay.State overlay,
        IBrowserSession? session,
        RenderOptions options,
        string title,
        string prompt,
        List<(string XPath, int Matches, bool IsAiSeed)> candidates,
        int initialCursor,
        bool allowBack,
        bool lensAvailable,
        CancellationToken ct)
    {
        // workspace-3uzl.6: mention the arrow-key synonyms; workspace-3uzl.4:
        // advertise 'h' back where a previous step exists.
        var hint = allowBack
            ? "j/k or ↑/↓: next candidate · Enter: confirm · h: back · Esc: cancel"
            : "j/k or ↑/↓: next candidate · Enter: confirm · Esc: cancel";
        var cursor = Math.Clamp(initialCursor, 0, candidates.Count - 1);
        while (!ct.IsCancellationRequested)
        {
            overlay.Card = BuildCard(title, prompt, candidates, cursor, null, hint, lensAvailable);
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
                case TunerKey.Back when allowBack:
                    return (StepOutcome.Back, null, cursor);
                case TunerKey.Confirm:
                    return (StepOutcome.Confirmed, candidates[cursor].XPath, cursor);
                case TunerKey.Cancel:
                    return (StepOutcome.Cancelled, null, cursor);
            }
        }

        return (StepOutcome.Cancelled, null, cursor);
    }

    private static async Task<(StepOutcome Outcome, List<string>? Choices, int Cursor)> RunMultiStepAsync(
        CommandContext ctx,
        SetupWizardOverlay.State overlay,
        IBrowserSession? session,
        RenderOptions options,
        string title,
        string prompt,
        List<(string XPath, int Matches, bool IsAiSeed)> candidates,
        int initialCursor,
        HashSet<int> selected,
        bool lensAvailable,
        CancellationToken ct)
    {
        if (candidates.Count == 0)
        {
            return (StepOutcome.Confirmed, [], 0);
        }

        const string hint = "j/k or ↑/↓: next · Space: mark · Enter: done · h: back · Esc: cancel";
        var cursor = Math.Clamp(initialCursor, 0, candidates.Count - 1);
        while (!ct.IsCancellationRequested)
        {
            overlay.Card = BuildCard(title, prompt, candidates, cursor, selected, hint, lensAvailable);
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
                case TunerKey.Back:
                    return (StepOutcome.Back, null, cursor);
                case TunerKey.Confirm:
                    return (StepOutcome.Confirmed, selected.Select(i => candidates[i].XPath).ToList(), cursor);
                case TunerKey.Cancel:
                    return (StepOutcome.Cancelled, null, cursor);
            }
        }

        return (StepOutcome.Cancelled, null, cursor);
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

        if (command.RawKeyChar is 'h')
        {
            // workspace-3uzl.4: back to the previous step (Backspace maps to
            // CommandType.GoBack, indistinguishable from Esc, so 'h' — the vim
            // sibling of j/k — is the dedicated back key).
            return TunerKey.Back;
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

    private static async Task<PersistOutcome> PersistAsync(
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
            return PersistOutcome.Fatal;
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
            // workspace-3uzl.3/.4: say what to DO about it — the tuner stays
            // open on step 3 so 'h' can step back to the body/headline picks.
            ctx.Logger.LogWarning("Tuner: self-test extraction returned null — not saving");
            ctx.NavigationService.ShowToast(
                ToastType.Error,
                "Layout NOT saved",
                "those selectors match nothing extractable — pick a different body or headline candidate (h steps back)");
            return PersistOutcome.SelfTestFailed;
        }

        var existing = await store.LoadAsync(domain).ConfigureAwait(false);
        var merged = ArticleLayoutCommandHandler.MergeByName(existing, candidate);
        await store.SaveAsync(merged).ConfigureAwait(false);
        ctx.Logger.LogInformation("Tuner: layout saved for {Domain} ({Entries} entries)", merged.Domain, merged.PageTypes.Count);

        // Toast, not status: the reader-view status bar's progress rule swallows
        // transient messages (pre-existing; tracked separately), toasts render
        // on every view.
        ctx.NavigationService.ShowToast(ToastType.Success, $"Layout tuned for {domain}", "future visits use it");
        return PersistOutcome.Saved;
    }

    private static async Task CancelAsync(CommandContext ctx, IBrowserSession? session)
    {
        ctx.NavigationService.SetStatusMessage("Layout tuning cancelled");
        await ClearLensHighlightAsync(session).ConfigureAwait(false);
    }

    /// <summary>
    /// workspace-3uzl.2: one up-front lens probe so the cards can state honestly
    /// whether matches highlight live or only counts are shown.
    /// </summary>
    private static async Task<bool> LensAvailableAsync(IBrowserSession? session)
    {
        if (session == null)
        {
            return false;
        }

        try
        {
            return await session.GetLensPageAsync().ConfigureAwait(false) != null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static async Task HighlightAsync(IBrowserSession? session, string xpath)
    {
        if (session == null)
        {
            return;
        }

        try
        {
            var lens = await session.GetLensPageAsync().ConfigureAwait(false);
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
            var lens = await session.GetLensPageAsync().ConfigureAwait(false);
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
