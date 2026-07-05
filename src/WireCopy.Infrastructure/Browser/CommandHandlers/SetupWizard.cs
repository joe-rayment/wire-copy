// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Browser.UI.Components;

namespace WireCopy.Infrastructure.Browser.CommandHandlers;

/// <summary>
/// workspace-6yb7.3 — the preview-first AI setup wizard.
/// Calls <see cref="IHierarchyAnalyzer.ProposeSetupQuestionsAsync"/> (round 1),
/// asks the model's clarifying questions (each option highlights its matches on
/// the sidecar lens), calls
/// <see cref="IHierarchyAnalyzer.InferPatternFromAnswersAsync"/> (round 2), and
/// then shows the RESULT: the candidate config is applied to the real link tree
/// behind a slim caption card, so what the user evaluates is the page they will
/// actually get. Enter saves exactly what is previewed; Space opens the adjust
/// loop (point at the lead / free-text guidance, each one budget-guarded
/// re-inference); Esc discards — document order is the implicit unconfigured
/// default and is never presented as a wizard option.
/// </summary>
internal static class SetupWizard
{
    /// <summary>Cap on structured question cards shown.</summary>
    internal const int MaxStructuredQuestions = 3;

    /// <summary>
    /// workspace-6yb7.6: minimum share of the page's story links a config must
    /// cover to earn a preview. Below this the wizard repairs or fails honestly.
    /// </summary>
    internal const double MinCoverageFraction = 0.10;

    /// <summary>
    /// workspace-45ji.3: the importance band (points) within which two story links
    /// count as equally prominent — a lead tie the screenshot can break.
    /// </summary>
    internal const int LeadAmbiguityMargin = 5;

    /// <summary>
    /// workspace-5vqk.5: the largest near-top band that still reads as a genuine LEAD
    /// CONTEST (a few candidates competing for the top). Above this the top band is a
    /// flat CO-EQUAL river — an aggregator's normal state — where electing one lead is
    /// meaningless, so the vision tiebreak is skipped.
    /// </summary>
    internal const int MaxLeadContestants = 4;

    /// <summary>Card-loop sentinel: Esc/quit (a chosen option is &gt;= 0).</summary>
    private const int CancelChoice = -1;

    /// <summary>Card-loop sentinel: Space on an adjustable card (the preview).</summary>
    private const int AdjustChoice = -2;

    /// <summary>Card-loop sentinel: 'z' (Undo) on the preview — revert the last refine.</summary>
    private const int UndoChoice = -3;

    /// <summary>workspace-5vqk.4: card-loop sentinel: 'x' on the preview — exclude the focused item.</summary>
    private const int ExcludeChoice = -4;

    /// <summary>
    /// Runs the wizard. Dependencies are injected (not pulled from CommandContext)
    /// so the flow is unit-testable with a mock analyzer + scripted input.
    /// </summary>
    /// <param name="render">Repaints the page + overlay (the overlay painter reads <paramref name="overlay"/>).</param>
    /// <param name="freeTextPrompt">Prompts the adjust loop's free-text card; returns null/empty to skip.</param>
    /// <param name="applyPreview">workspace-6yb7.3: builds the candidate config into the page's REAL
    /// link tree so the preview card sits over the actual result. Null (tests) skips the live preview;
    /// the caller is responsible for restoring the original tree when the wizard is cancelled.</param>
    /// <param name="lens">workspace-wylw: best-effort sidecar-lens preview hooks — the focused
    /// option's matches highlight live on the page; null (tests / no sidecar) degrades to text-only.</param>
    public static async Task<Result> RunAsync(
        IHierarchyAnalyzer analyzer,
        IInputHandler input,
        Func<CancellationToken, Task> render,
        SetupWizardOverlay.State overlay,
        List<LinkInfo> links,
        string pageUrl,
        byte[]? screenshot,
        ModelRoundTripBudget budget,
        Func<CancellationToken, Task<string?>> freeTextPrompt,
        Func<CancellationToken, Task<LinkInfo?>>? pickLeadFromTree,
        Func<SiteHierarchyConfig, CancellationToken, Task>? applyPreview,
        Lens? lens,
        CancellationToken ct,
        SiteHierarchyConfig? existingConfig = null,
        Func<CancellationToken, Task<string?>>? promptLeadUrl = null)
    {
        ArgumentNullException.ThrowIfNull(analyzer);
        ArgumentNullException.ThrowIfNull(budget);

        // ---- workspace-9k27.4: re-entry on an already-configured site seeds
        // the preview from the SAVED layout, so a small tweak refines the
        // config the previous session perfected instead of regenerating from
        // scratch (which threw away an almost-perfect layout — the very defect
        // the incremental-refine work fixed WITHIN a session). Only when the
        // saved config still matches this page; a dead config falls through to
        // the full wizard.
        if (existingConfig != null
            && existingConfig.Sections.Count > 0
            && !IsDegenerate(existingConfig, links))
        {
            return await RunPreviewLoopAsync(
                analyzer,
                input,
                render,
                overlay,
                links,
                pageUrl,
                screenshot,
                proposal: EmptyProposal(),
                answers: new List<SetupAnswer>(),
                config: existingConfig,
                confirmQuestion: null,
                budget,
                freeTextPrompt,
                pickLeadFromTree,
                applyPreview,
                lens,
                ct,
                promptLeadUrl).ConfigureAwait(false);
        }

        // ---- Round 1: propose pattern + questions ----
        if (!budget.TrySpend())
        {
            return new Result { Cancelled = true };
        }

        var proposal = await RunWithProgressAsync(
            analyzer.ProposeSetupQuestionsAsync(screenshot, links, pageUrl, ct),
            "Reading the page",
            overlay,
            render,
            ct).ConfigureAwait(false);

        // ---- Up to MaxStructuredQuestions clarifying-question cards ----
        // workspace-6yb7.4: only DISCRIMINATING questions survive — a question
        // must offer concrete, visually inspectable alternatives whose answers
        // change the layout. Confirmation theater is dropped client-side even
        // when the model emits it.
        var answers = new List<SetupAnswer>();
        var questions = proposal.Questions
            .Where(IsDiscriminating)
            .Take(MaxStructuredQuestions)
            .ToList();
        for (var qi = 0; qi < questions.Count; qi++)
        {
            var card = BuildQuestionCard(questions[qi], qi + 1, questions.Count);
            var choice = await RunCardAsync(input, render, overlay, card, lens, ct).ConfigureAwait(false);
            if (choice < 0)
            {
                return new Result { Cancelled = true };
            }

            // workspace-wylw: echo the chosen option's durable identifier back to
            // round 2, so the model grounds the answer in the selector the user
            // confirmed rather than re-deriving it from the label alone.
            var chosen = card.Options[Math.Clamp(choice, 0, card.Options.Count - 1)];
            answers.Add(new SetupAnswer
            {
                QuestionId = questions[qi].Id,
                Answer = chosen.Identifier.Length > 0 ? $"{chosen.Label} ({chosen.Identifier})" : chosen.Label,
            });
        }

        // ---- Round 2: infer the durable pattern from the answers ----
        if (!budget.TrySpend())
        {
            return new Result { Cancelled = true };
        }

        var inferred = await RunWithProgressAsync(
            analyzer.InferPatternFromAnswersAsync(screenshot, links, pageUrl, proposal, answers, ct),
            "Building your layout",
            overlay,
            render,
            ct).ConfigureAwait(false);
        var config = inferred.Config;
        var confirmQuestion = inferred.ConfirmQuestion;

        // ---- workspace-6yb7.6: degenerate gate — self-test BEFORE showing
        // anything. A config covering (almost) none of the live page's story
        // links gets ONE automatic repair round-trip with the mismatch fed
        // back as a structured answer; a config that is still degenerate goes
        // to the honest failure card below, never to the preview.
        if (IsDegenerate(config, links) && budget.TrySpend())
        {
            answers.Add(SelfTestFailureAnswer(config, links));
            inferred = await RunWithProgressAsync(
                analyzer.InferPatternFromAnswersAsync(screenshot, links, pageUrl, proposal, answers, ct),
                "Rechecking the page",
                overlay,
                render,
                ct).ConfigureAwait(false);
            config = inferred.Config;
            confirmQuestion = inferred.ConfirmQuestion;
        }

        // ---- workspace-romy.5: ordering/lead sanity gate. Coverage alone let
        // an ads-first layout through; this catches a weak lead, sponsor slots
        // outranking stories, and dropped high-importance links, with ONE
        // automatic repair round-trip. A config that is still unsound is
        // previewed honestly (the user can see and adjust it) rather than
        // silently saved-as-good.
        if (!IsDegenerate(config, links) &&
            OrderingSanityFailure(config, links) is { } sanityFailure &&
            budget.TrySpend())
        {
            answers.Add(sanityFailure);
            inferred = await RunWithProgressAsync(
                analyzer.InferPatternFromAnswersAsync(screenshot, links, pageUrl, proposal, answers, ct),
                "Reordering the layout",
                overlay,
                render,
                ct).ConfigureAwait(false);
            config = inferred.Config;
            confirmQuestion = inferred.ConfirmQuestion;
        }

        // ---- workspace-45ji.3: OPTIONAL vision tiebreak for the LEAD. The
        // deterministic gates above stay primary; only when importance leaves a
        // cluster of near-equally-prominent candidates AND a screenshot exists do
        // we spend ONE call to ask which is visually dominant, then durably reorder
        // so its section leads. A -1 (or any failure) keeps the deterministic lead.
        if (screenshot is { Length: > 0 } &&
            !IsDegenerate(config, links) &&
            LeadIsAmbiguous(config, links) &&
            budget.TrySpend())
        {
            var candidates = LeadCandidates(config, links);
            var chosen = await RunWithProgressAsync(
                analyzer.VerifyLeadWithVisionAsync(screenshot, candidates, pageUrl, ct),
                "Checking the lead",
                overlay,
                render,
                ct).ConfigureAwait(false);
            if (chosen >= 0 && chosen < candidates.Count)
            {
                config = PromoteLeadSection(config, candidates[chosen], links);
            }
        }

        // ---- Preview loop: show the RESULT, not a description of it ----
        // (extracted to RunPreviewLoopAsync so wizard re-entry can seed it
        // directly from a saved config — workspace-9k27.4)
        return await RunPreviewLoopAsync(
            analyzer,
            input,
            render,
            overlay,
            links,
            pageUrl,
            screenshot,
            proposal,
            answers,
            config,
            confirmQuestion,
            budget,
            freeTextPrompt,
            pickLeadFromTree,
            applyPreview,
            lens,
            ct,
            promptLeadUrl).ConfigureAwait(false);
    }

    /// <summary>
    /// workspace-6yb7.4: a question earns a card only when it discriminates —
    /// at least two distinctly labelled alternatives, at least one of them
    /// carrying a durable identifier the lens can light up. Kills the
    /// synthesized yes/no confirmation cards (no options = nothing to show on
    /// the page, nothing to decide) and un-highlightable abstract questions.
    /// Note a hide-or-keep question legitimately points BOTH options at the
    /// same element — the verdict differs, so identifiers may repeat.
    /// </summary>
    internal static bool IsDiscriminating(SetupQuestion question)
    {
        ArgumentNullException.ThrowIfNull(question);
        if (question.Options.Count < 2)
        {
            return false;
        }

        var hasIdentifier = question.Options.Any(o =>
            !string.IsNullOrWhiteSpace(o.ParentSelector) || !string.IsNullOrWhiteSpace(o.UrlPattern));
        if (!hasIdentifier)
        {
            return false;
        }

        // Two options with the same label are one option.
        return question.Options
            .Select(o => o.Label.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count() > 1;
    }

    /// <summary>
    /// Coverage self-test: (covered, total) story links under the SAME
    /// <see cref="NavigationTreeBuilder.MatchesSection"/> semantics revisits use.
    /// </summary>
    internal static (int Covered, int Total) Coverage(SiteHierarchyConfig config, List<LinkInfo> links)
    {
        ArgumentNullException.ThrowIfNull(config);
        var contentLinks = links.Where(l => l.Type == LinkType.Content && !l.IsGroupHeader).ToList();
        var covered = contentLinks.Count(l => config.Sections.Any(sec => NavigationTreeBuilder.MatchesSection(l, sec)));
        return (covered, contentLinks.Count);
    }

    /// <summary>
    /// workspace-6yb7.6: a config is degenerate when it has no sections at all,
    /// or its sections match (almost) none of the live page's story links —
    /// the Techmeme failure mode where a confidently-presented layout was
    /// empty in practice. Pages with no story links can't be coverage-judged;
    /// only a sectionless config is degenerate there.
    /// </summary>
    internal static bool IsDegenerate(SiteHierarchyConfig config, List<LinkInfo> links)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.Sections.Count == 0)
        {
            return true;
        }

        var (covered, total) = Coverage(config, links);
        if (total == 0)
        {
            return false;
        }

        return (double)covered / total < MinCoverageFraction;
    }

    /// <summary>
    /// workspace-romy.5: ordering/lead sanity — the coverage gate alone passed
    /// layouts that led with promo slots and dropped most stories (the techmeme
    /// report). Returns a structured repair answer naming the violated rule, or
    /// null when the config is sound. Rules:
    /// (1) the lead (first) section must contain at least one genuinely
    ///     prominent link — top-quartile importance, or above-fold with
    ///     headline-sized type;
    /// (2) no sponsor-majority section may rank above a real story section;
    /// (3) at least 70% of high-importance links (score 80+ — the base Content
    ///     score is 70, so 70 would sweep in every short publisher-tag link)
    ///     must be covered when the page has enough of them to judge.
    /// </summary>
    internal static SetupAnswer? OrderingSanityFailure(SiteHierarchyConfig config, List<LinkInfo> links)
    {
        ArgumentNullException.ThrowIfNull(config);
        var contentLinks = links.Where(l => l.Type == LinkType.Content && !l.IsGroupHeader).ToList();
        if (config.Sections.Count == 0 || contentLinks.Count == 0)
        {
            return null;
        }

        List<LinkInfo> MatchesOf(HierarchySection s) =>
            contentLinks.Where(l => NavigationTreeBuilder.MatchesSection(l, s)).ToList();

        // Rule 1: lead prominence.
        var leadMatches = MatchesOf(config.Sections[0]);
        if (leadMatches.Count > 0)
        {
            var scores = contentLinks.Select(l => l.ImportanceScore).OrderByDescending(s => s).ToList();
            var topQuartileScore = scores[Math.Max(0, (scores.Count - 1) / 4)];
            var leadIsProminent = leadMatches.Any(l =>
                l.ImportanceScore >= topQuartileScore ||
                (l.Geometry is { AboveFold: true, FontSize: >= 17 }));
            if (!leadIsProminent)
            {
                var best = leadMatches.OrderByDescending(l => l.ImportanceScore).First();
                return new SetupAnswer
                {
                    QuestionId = "ordering-sanity-failure",
                    Answer = $"Sanity check failed: your FIRST section \"{config.Sections[0].Name}\" contains no " +
                             $"prominent link (best is \"{Truncate(best.DisplayText, 60)}\", score {best.ImportanceScore}, " +
                             $"while the page's top-quartile score is {topQuartileScore}). The lead section must hold " +
                             "the page's visually dominant story — re-derive the sections so a high-score / " +
                             "above-fold, large-font link leads, and move promo or rail content down or out.",
                };
            }
        }

        // Rule 2: sponsor-majority sections must not outrank story sections.
        var sponsorMajorityIndex = -1;
        for (var i = 0; i < config.Sections.Count; i++)
        {
            var matches = MatchesOf(config.Sections[i]);
            if (matches.Count == 0)
            {
                continue;
            }

            var sponsorMajority = matches.Count(l => l.IsSponsored) * 2 > matches.Count;
            if (sponsorMajority && sponsorMajorityIndex < 0)
            {
                sponsorMajorityIndex = i;
            }
            else if (!sponsorMajority && sponsorMajorityIndex >= 0)
            {
                return new SetupAnswer
                {
                    QuestionId = "ordering-sanity-failure",
                    Answer = $"Sanity check failed: section \"{config.Sections[sponsorMajorityIndex].Name}\" is mostly " +
                             $"flag=sponsor links but ranks ABOVE the story section \"{config.Sections[i].Name}\". " +
                             "Sponsored/promo slots must rank last or be excluded entirely (prefer excluding their " +
                             "container by parent selector). Re-emit the sections with real stories first.",
                };
            }
        }

        // Rule 3: high-importance coverage. 80+ because 70 is the BASE score
        // for every Content link — real stories earn boosts past it.
        var highScore = contentLinks.Where(l => l.ImportanceScore >= 80).ToList();
        if (highScore.Count >= 3)
        {
            var covered = highScore.Count(l => config.Sections.Any(s => NavigationTreeBuilder.MatchesSection(l, s)));
            if ((double)covered / highScore.Count < 0.7)
            {
                var missed = highScore.First(l => !config.Sections.Any(s => NavigationTreeBuilder.MatchesSection(l, s)));
                return new SetupAnswer
                {
                    QuestionId = "ordering-sanity-failure",
                    Answer = $"Sanity check failed: your sections cover only {covered} of the page's {highScore.Count} " +
                             $"high-importance links (score 80+), e.g. \"{Truncate(missed.DisplayText, 60)}\" " +
                             $"(parent: {missed.ParentSelector ?? "-"}) is not matched by any section. These are the " +
                             "page's top stories — broaden the section identifiers so the top-story river is covered.",
                };
            }
        }

        return null;
    }

    /// <summary>
    /// The near-equally-prominent lead candidates (story-shaped Content links within
    /// <see cref="LeadAmbiguityMargin"/> of the top importance), highest first, capped.
    /// </summary>
    internal static List<LinkInfo> LeadCandidates(SiteHierarchyConfig config, List<LinkInfo> links)
    {
        ArgumentNullException.ThrowIfNull(config);
        var content = links
            .Where(l => l.Type == LinkType.Content && !l.IsGroupHeader && !l.IsSponsored)
            .ToList();
        if (content.Count == 0)
        {
            return new List<LinkInfo>();
        }

        var max = content.Max(l => l.ImportanceScore);
        return content
            .Where(l => max - l.ImportanceScore <= LeadAmbiguityMargin)
            .OrderByDescending(l => l.ImportanceScore)
            .Take(6)
            .ToList();
    }

    /// <summary>
    /// workspace-5vqk.5: the layout has a genuine LEAD CONTEST the deterministic
    /// ranking can't decide — a FEW near-equally-prominent candidates competing for
    /// the top, worth ONE screenshot tiebreak (workspace-45ji.3). Critically it is
    /// FALSE on a flat aggregator, where a broad band of stories share the top score:
    /// there is no single lead to elect, so the old "≥2 within margin" test fired the
    /// vision call on Techmeme's normal co-equal state and mis-promoted an arbitrary
    /// story. A contest is a SMALL near-top band (≤ <see cref="MaxLeadContestants"/>);
    /// a broad band is co-equal and the deterministic order stands.
    /// </summary>
    internal static bool LeadIsAmbiguous(SiteHierarchyConfig config, List<LinkInfo> links)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.Sections.Count == 0)
        {
            return false;
        }

        var stories = links
            .Where(l => l.Type == LinkType.Content && !l.IsGroupHeader && !l.IsSponsored)
            .ToList();
        if (stories.Count == 0)
        {
            return false;
        }

        var max = stories.Max(l => l.ImportanceScore);
        var nearTop = stories.Count(l => max - l.ImportanceScore <= LeadAmbiguityMargin);
        return nearTop >= 2 && nearTop <= MaxLeadContestants;
    }

    /// <summary>
    /// Durable, non-destructive lead promotion: move the section that contains
    /// <paramref name="lead"/> to the front (renumbering SortOrder). No-op when the
    /// lead's section is already first or not found — every other section is kept.
    /// </summary>
    internal static SiteHierarchyConfig PromoteLeadSection(SiteHierarchyConfig config, LinkInfo lead, List<LinkInfo> links)
    {
        ArgumentNullException.ThrowIfNull(config);
        var idx = config.Sections.FindIndex(s => NavigationTreeBuilder.MatchesSection(lead, s));
        if (idx <= 0)
        {
            return config;
        }

        var reordered = new List<HierarchySection> { config.Sections[idx] };
        for (var i = 0; i < config.Sections.Count; i++)
        {
            if (i != idx)
            {
                reordered.Add(config.Sections[i]);
            }
        }

        for (var i = 0; i < reordered.Count; i++)
        {
            reordered[i] = reordered[i] with { SortOrder = i };
        }

        return config with { Sections = reordered };
    }

    /// <summary>
    /// workspace-6yb7.6: the structured answer fed back for the single automatic
    /// repair round-trip — tells the model its identifiers missed the live DOM
    /// and anchors the retry in the link list's actual parent fields.
    /// </summary>
    internal static SetupAnswer SelfTestFailureAnswer(SiteHierarchyConfig config, List<LinkInfo> links)
    {
        var (covered, total) = Coverage(config, links);
        return new SetupAnswer
        {
            QuestionId = "self-test-failure",
            Answer = $"Self-test failed: your previous pattern matched only {covered} of {total} story links " +
                     "on the live page — the selectors/url patterns did not match the page's actual DOM. " +
                     "Re-derive every section identifier by copying parent-selector fragments EXACTLY from " +
                     "the links' parent fields in the list above, and drop any url_pattern that does not " +
                     "literally appear in those links' URLs.",
        };
    }

    /// <summary>
    /// The preview caption — one row per durable section with its REAL match
    /// count against the current page's links (the same
    /// <see cref="NavigationTreeBuilder.MatchesSection"/> semantics revisits use),
    /// so a degenerate config is visible before it is saved. Focusing a row
    /// highlights all of its matched links on the sidecar lens; the page behind
    /// the card already shows the candidate tree.
    /// </summary>
    internal static SetupWizardOverlay.WizardCard BuildPreviewCard(
        SiteHierarchyConfig config,
        List<LinkInfo> links,
        int? previousCovered = null,
        SetupQuestion? confirmQuestion = null,
        bool canUndo = false,
        string? notice = null)
    {
        var contentLinks = links.Where(l => l.Type == LinkType.Content && !l.IsGroupHeader).ToList();

        // workspace-5vqk.3/.4: render the REAL extracted headlines per section (built
        // by BuildPreviewRows, which is exclude-aware so an 'x'-excluded item vanishes
        // from the list). The confirm-question rows are appended AFTER the layout rows.
        var options = BuildPreviewRows(config, links).Select(r => r.Option).ToList();

        // workspace-romy.8: the confirm question's options render as answerable
        // rows after the layout rows; the prompt line carries the question. The
        // cursor stays on row 0 (a section header), so Enter still saves as-is.
        if (confirmQuestion != null)
        {
            options.AddRange(confirmQuestion.Options.Select(o => new SetupWizardOverlay.CardOption
            {
                Label = $"AI asks · {o.Label}",
                Identifier = FormatIdentifier(o.ParentSelector, o.UrlPattern),
                HighlightSelector = CssForIdentifier(o.ParentSelector, o.UrlPattern),
            }));
        }

        var covered = CoveredCount(config, contentLinks);
        var footnote = covered == 0 && contentLinks.Count > 0
            ? "⚠ No links on this page match this layout"
            : $"{covered} of {contentLinks.Count} story links covered";

        // workspace-romy.7: after an adjustment, show the coverage delta so the
        // user can see whether the round actually changed anything.
        if (previousCovered is { } prev && prev != covered && contentLinks.Count > 0)
        {
            footnote += $" · was {prev}";
        }

        // workspace-9k27.2: the over-exclusion guard vetoed rule(s) — tell the
        // user WHY their "hide X" steering didn't take instead of silence.
        if (config.DroppedExcludeRuleCount > 0)
        {
            footnote += $" · ⚠ {config.DroppedExcludeRuleCount} exclusion(s) skipped — too broad, would hide real stories";
        }

        // workspace-5vqk.5: SURFACE — never silently pass — a substantial population
        // of same-shaped stories left uncovered (a second cluster the layout doesn't
        // reach yet), inviting another seed pick (5vqk.6). Relativized so a
        // fully-covered single-pattern page is never nagged.
        var uncovered = UncoveredStoryShaped(config, contentLinks);
        if (uncovered >= Math.Max(5, covered / 5))
        {
            footnote += $" · {uncovered} more story-shaped link(s) uncovered — Space to add another pattern";
        }

        // workspace-r8on: a transient notice (a refused 'x', a wrong-row press) rides
        // the footnote for one render so a no-op key never leaves the user guessing.
        if (!string.IsNullOrEmpty(notice))
        {
            footnote = $"⚠ {notice}";
        }

        // workspace-5vqk.3: the count-as-header line the user reads first.
        string headline;
        if (confirmQuestion != null)
        {
            headline = $"AI is unsure: {confirmQuestion.Prompt} Answer below — or save as shown.";
        }
        else if (covered > 0)
        {
            headline = $"Learned this pattern — {covered} {(covered == 1 ? "story" : "stories")} across {config.Sections.Count} section(s)";
        }
        else
        {
            headline = "No stories matched this layout yet";
        }

        return new SetupWizardOverlay.WizardCard
        {
            Title = "Your new layout",
            Prompt = headline,
            Options = options,
            Footnote = footnote,
            Hint = canUndo
                ? "↑/↓ browse · Enter save · Space refine · x drop item · z undo · Esc discard"
                : "↑/↓ browse · Enter save · Space adjust · x drop item · Esc discard",
        };
    }

    /// <summary>
    /// workspace-5vqk.3/.4: the ordered preview rows — one header per section
    /// followed by one row per matched, NON-EXCLUDED link (carrying that link so an
    /// 'x' on the row can exclude it). The same source of truth the preview card and
    /// the item-exclude handler both read, so the rows the user sees are exactly the
    /// links that will save.
    /// </summary>
    internal static List<PreviewRow> BuildPreviewRows(SiteHierarchyConfig config, List<LinkInfo> links)
    {
        ArgumentNullException.ThrowIfNull(config);
        var contentLinks = links.Where(l => l.Type == LinkType.Content && !l.IsGroupHeader).ToList();
        var rows = new List<PreviewRow>();
        foreach (var section in config.Sections)
        {
            var matched = contentLinks
                .Where(l => NavigationTreeBuilder.MatchesSection(l, section) && !NavigationTreeBuilder.IsExcluded(l, config))
                .ToList();
            rows.Add(new PreviewRow(
                new SetupWizardOverlay.CardOption
                {
                    Label = $"{section.Name} — {matched.Count} {(matched.Count == 1 ? "story" : "stories")}",
                    Identifier = string.Join(" · ", section.ParentSelectors.Concat(section.UrlPatterns)),
                    HighlightSelector = CssForSection(section),
                },
                null));
            foreach (var link in matched)
            {
                rows.Add(new PreviewRow(
                    new SetupWizardOverlay.CardOption
                    {
                        Label = $"   • {Truncate(CollapseWhitespace(link.DisplayText), 72)}",
                        HighlightSelector = CssForLink(link),
                    },
                    link));
            }
        }

        return rows;
    }

    /// <summary>Content links a config actually shows: matched by a section AND not excluded.</summary>
    internal static int CoveredCount(SiteHierarchyConfig config, List<LinkInfo> contentLinks) =>
        contentLinks.Count(l => !NavigationTreeBuilder.IsExcluded(l, config)
            && config.Sections.Any(sec => NavigationTreeBuilder.MatchesSection(l, sec)));

    /// <summary>
    /// workspace-5vqk.5: story-shaped content links (real headline text, not
    /// sponsored) that NO section covers and no rule excludes — a same-shaped cluster
    /// the current pattern misses. Drives the preview's "add another pattern" nudge.
    /// </summary>
    internal static int UncoveredStoryShaped(SiteHierarchyConfig config, List<LinkInfo> contentLinks) =>
        contentLinks.Count(l =>
            !l.IsSponsored
            && (l.DisplayText?.Length ?? 0) >= LeadOverrideDerivation.MinStoryTextLength
            && !NavigationTreeBuilder.IsExcluded(l, config)
            && !config.Sections.Any(sec => NavigationTreeBuilder.MatchesSection(l, sec)));

    /// <summary>
    /// workspace-5vqk.4: deterministically excludes ONE previewed item (an 'x' on a
    /// row) and its token-siblings, WITHOUT a model round-trip. Derives distinctive
    /// exclude tokens from the item's parent selector, keeping only tokens that match
    /// NO other kept story (the <see cref="LeadOverrideDerivation"/> guard, so a real
    /// story is never erased); falls back to a distinctive URL segment when no token
    /// qualifies. Honors the 25% high-importance cap via
    /// <see cref="NavigationTreeBuilder.GuardExcludeRules"/>. Returns null (REFUSED)
    /// when the item can't be excluded without also hiding a real story, or when the
    /// exclusion would be a net no-op.
    /// </summary>
    internal static SiteHierarchyConfig? ExcludeItem(SiteHierarchyConfig config, LinkInfo item, List<LinkInfo> links)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(item);
        var contentLinks = links.Where(l => l.Type == LinkType.Content && !l.IsGroupHeader).ToList();

        bool Covered(LinkInfo l) => config.Sections.Any(s => NavigationTreeBuilder.MatchesSection(l, s));
        bool IsStory(LinkInfo l) =>
            l.Type == LinkType.Content && !l.IsSponsored && (l.DisplayText?.Length ?? 0) >= LeadOverrideDerivation.MinStoryTextLength;

        // Every real story still on the page EXCEPT the item — an exclude token/pattern
        // may never match one of these (that would erase a kept story).
        var keptStories = contentLinks
            .Where(l => Covered(l) && IsStory(l)
                && !ReferenceEquals(l, item)
                && !string.Equals(l.Url, item.Url, StringComparison.Ordinal))
            .ToList();

        bool SafeToken(string t) =>
            keptStories.All(s => string.IsNullOrEmpty(s.ParentSelector)
                || !s.ParentSelector.Contains(t, StringComparison.OrdinalIgnoreCase));
        bool SafePattern(string p) =>
            keptStories.All(s => !s.Url.Contains(p, StringComparison.OrdinalIgnoreCase));

        var tokens = SelectorDerivation.DiscriminatingTokens(item.ParentSelector)
            .Where(SafeToken)
            .Distinct(StringComparer.Ordinal)
            .Where(t => !config.ExcludeSelectors.Contains(t, StringComparer.OrdinalIgnoreCase))
            .ToList();

        var urlPatterns = new List<string>();
        if (tokens.Count == 0)
        {
            // No selector token separates the item from the kept stories — try a
            // distinctive URL segment that hits no kept story instead.
            urlPatterns = SelectorDerivation.MeaningfulPathSegments(item.Url)
                .Select(seg => $"/{seg}/")
                .Where(SafePattern)
                .Where(p => !config.ExcludeUrlPatterns.Contains(p, StringComparer.OrdinalIgnoreCase))
                .Distinct(StringComparer.Ordinal)
                .Take(2)
                .ToList();
        }

        if (tokens.Count == 0 && urlPatterns.Count == 0)
        {
            return null; // REFUSED — nothing distinctive that spares the kept stories
        }

        var candidate = config with
        {
            ExcludeSelectors = config.ExcludeSelectors.Concat(tokens).Distinct(StringComparer.Ordinal).ToList(),
            ExcludeUrlPatterns = config.ExcludeUrlPatterns.Concat(urlPatterns).Distinct(StringComparer.Ordinal).ToList(),
        };

        // Backstop: the 25% high-importance cap drops any rule that turns out to be
        // too broad on THIS page (belt-and-suspenders over the per-token guard).
        var guarded = NavigationTreeBuilder.GuardExcludeRules(candidate, contentLinks);
        var dropped = (candidate.ExcludeSelectors.Count - guarded.ExcludeSelectors.Count)
            + (candidate.ExcludeUrlPatterns.Count - guarded.ExcludeUrlPatterns.Count);

        // If the guard vetoed everything we added, the item is still shown — refuse.
        if (!NavigationTreeBuilder.IsExcluded(item, guarded))
        {
            return null;
        }

        return guarded with { DroppedExcludeRuleCount = config.DroppedExcludeRuleCount + dropped };
    }

    /// <summary>workspace-5vqk.3: highlights ONE specific link on the lens by an href substring.</summary>
    internal static string CssForLink(LinkInfo link)
    {
        ArgumentNullException.ThrowIfNull(link);
        var url = link.Url ?? string.Empty;
        if (url.Length == 0)
        {
            return string.Empty;
        }

        var fragment = Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.AbsolutePath.Length > 1
            ? uri.AbsolutePath
            : url;
        return $"a[href*=\"{EscapeAttrValue(fragment)}\"]";
    }

    /// <summary>
    /// workspace-wylw: CSS evaluated on the lens to show the links a durable
    /// identifier pair matches — descendant links of the parent-selector fragment
    /// plus href-substring matches for the URL pattern (approximating the
    /// Contains semantics of <see cref="NavigationTreeBuilder.MatchesSection"/>).
    /// </summary>
    internal static string CssForIdentifier(string parentSelector, string urlPattern)
    {
        var parts = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(parentSelector))
        {
            parts.Add($"{parentSelector.Trim()} a[href]");
        }

        if (!string.IsNullOrWhiteSpace(urlPattern))
        {
            parts.Add($"a[href*=\"{EscapeAttrValue(urlPattern.Trim())}\"]");
        }

        return string.Join(", ", parts);
    }

    /// <summary>workspace-wylw: <see cref="CssForIdentifier"/> over a whole section's identifier lists.</summary>
    internal static string CssForSection(HierarchySection section)
    {
        ArgumentNullException.ThrowIfNull(section);
        var parts = section.ParentSelectors
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => $"{x.Trim()} a[href]")
            .Concat(section.UrlPatterns
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => $"a[href*=\"{EscapeAttrValue(x.Trim())}\"]"));
        return string.Join(", ", parts);
    }

    /// <summary>
    /// workspace-5oe9.9: maps a link the user pointed at into a durable
    /// <see cref="HierarchySection"/> via <see cref="SelectorDerivation"/>.
    /// Returns null for a synthetic header/group node (the caller shows "pick a
    /// story, not a header") or when no durable identifier can be derived.
    /// </summary>
    internal static HierarchySection? SectionFromPickedLink(LinkInfo link, string name)
    {
        ArgumentNullException.ThrowIfNull(link);
        if (link.IsGroupHeader)
        {
            return null;
        }

        var selectors = SelectorDerivation.DeriveParentSelectors(new[] { link });
        var urlPatterns = SelectorDerivation.DeriveUrlPatterns(new[] { link });
        if (selectors.Count == 0 && urlPatterns.Count == 0)
        {
            return null;
        }

        return new HierarchySection
        {
            Name = name,
            SortOrder = 0,
            ParentSelectors = selectors,
            UrlPatterns = urlPatterns,
        };
    }

    /// <summary>
    /// workspace-5oe9.9: turns a picked link into the lead-override answer fed
    /// back to round 2. Returns null for header/synthetic picks or when no
    /// durable identifier can be derived.
    /// </summary>
    internal static SetupAnswer? LeadOverrideAnswer(LinkInfo pickedLead)
    {
        var section = SectionFromPickedLink(pickedLead, "Top Story");
        if (section == null)
        {
            return null;
        }

        var identifier = string.Join(" · ", section.ParentSelectors.Concat(section.UrlPatterns));
        return new SetupAnswer
        {
            QuestionId = "lead-override",
            Answer = $"The single main/lead story is the link matching {identifier} (\"{pickedLead.DisplayText}\"). " +
                     "Put it alone in the Top Story section.",
        };
    }

    /// <summary>
    /// workspace-cbjx.2: resolves a user-typed URL to the page link it names, so
    /// the lead can be set by URL instead of scrolling. Matches on a normalized
    /// URL (scheme/www/query/fragment/trailing-slash stripped): exact first, then
    /// either-direction containment; ties break toward real Content and higher
    /// importance. Returns null when nothing plausibly matches.
    /// </summary>
    internal static LinkInfo? ResolveLeadByUrl(string url, List<LinkInfo> links)
    {
        if (string.IsNullOrWhiteSpace(url) || links == null || links.Count == 0)
        {
            return null;
        }

        var target = NormalizeUrl(url);
        if (target.Length == 0)
        {
            return null;
        }

        var candidates = links
            .Where(l => !l.IsGroupHeader && !string.IsNullOrEmpty(l.Url))
            .ToList();

        var exact = candidates
            .Where(l => NormalizeUrl(l.Url) == target)
            .OrderByDescending(l => l.Type == LinkType.Content)
            .ThenByDescending(l => l.ImportanceScore)
            .FirstOrDefault();
        if (exact != null)
        {
            return exact;
        }

        return candidates
            .Select(l => (Link: l, Norm: NormalizeUrl(l.Url)))
            .Where(x => x.Norm.Length > 0 && (x.Norm.Contains(target, StringComparison.Ordinal) || target.Contains(x.Norm, StringComparison.Ordinal)))
            .OrderByDescending(x => x.Link.Type == LinkType.Content)
            .ThenByDescending(x => x.Norm.Length) // the most specific match
            .ThenByDescending(x => x.Link.ImportanceScore)
            .Select(x => x.Link)
            .FirstOrDefault();
    }

    /// <summary>Strips scheme, leading www., query, fragment and trailing slash; lower-cases.</summary>
    internal static string NormalizeUrl(string url)
    {
        var s = url.Trim();
        if (s.Length == 0)
        {
            return s;
        }

        var scheme = s.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            s = s[(scheme + 3)..];
        }

        var hash = s.IndexOf('#', StringComparison.Ordinal);
        if (hash >= 0)
        {
            s = s[..hash];
        }

        var query = s.IndexOf('?', StringComparison.Ordinal);
        if (query >= 0)
        {
            s = s[..query];
        }

        if (s.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
        {
            s = s[4..];
        }

        return s.TrimEnd('/').ToLowerInvariant();
    }

    /// <summary>
    /// workspace-6yb7.6: the honest failure card shown instead of a preview
    /// when the config (still) matches ~none of the page's story links.
    /// </summary>
    internal static AdjustCardShape BuildFailureCardShape(
        SiteHierarchyConfig config, List<LinkInfo> links, ModelRoundTripBudget budget)
    {
        // workspace-f25j.1: "couldn't find a layout that MATCHES" contradicted a
        // footnote showing a nonzero match count — the honest claim is about
        // RELIABLE coverage, with the footnote carrying the exact numbers.
        var (covered, total) = Coverage(config, links);
        return new AdjustCardShape(
            Title: "No reliable pattern found",
            Prompt: "I couldn't find a layout that reliably covers this page's stories.",
            Footnote: budget.Exhausted
                ? $"Matches {covered} of {total} story links · AI budget used up — Esc to leave unconfigured"
                : $"The proposed layout matches {covered} of {total} story links on this page",
            Hint: "↑/↓ choose · Enter select · Esc leave unconfigured");
    }

    /// <summary>
    /// workspace-5vqk.2: test seam for the deterministic pick→river builder so the
    /// merge/neutral-name/no-MaxLinks contract can be asserted against the real
    /// techmeme fixture without a model round-trip.
    /// </summary>
    internal static InferredPattern? DeriveLeadOverrideForTest(
        LinkInfo lead, List<LinkInfo> links, SiteHierarchyConfig currentConfig) =>
        BuildDeterministicLeadOverride(lead, links, currentConfig);

    /// <summary>workspace-9k27.4: minimal proposal for re-entry seeding — the
    /// repair/confirm paths need one, but a saved config has no round-1 output.</summary>
    private static SiteSetupProposal EmptyProposal() => new()
    {
        ProposedPattern = new ProposedPattern(),
    };

    /// <summary>
    /// The preview/refine loop: renders the layout-as-result card, routes
    /// adjust/undo/confirm-question actions, and returns the saved config or a
    /// cancellation. Entered after rounds 1-2 on a fresh setup, or DIRECTLY
    /// with the saved config on wizard re-entry (workspace-9k27.4).
    /// </summary>
    private static async Task<Result> RunPreviewLoopAsync(
        IHierarchyAnalyzer analyzer,
        IInputHandler input,
        Func<CancellationToken, Task> render,
        SetupWizardOverlay.State overlay,
        List<LinkInfo> links,
        string pageUrl,
        byte[]? screenshot,
        SiteSetupProposal proposal,
        List<SetupAnswer> answers,
        SiteHierarchyConfig config,
        SetupQuestion? confirmQuestion,
        ModelRoundTripBudget budget,
        Func<CancellationToken, Task<string?>> freeTextPrompt,
        Func<CancellationToken, Task<LinkInfo?>>? pickLeadFromTree,
        Func<SiteHierarchyConfig, CancellationToken, Task>? applyPreview,
        Lens? lens,
        CancellationToken ct,
        Func<CancellationToken, Task<string?>>? promptLeadUrl = null)
    {
        // workspace-romy.7: previousCovered carries the last previewed config's
        // coverage into the next preview so each adjustment round shows what
        // changed ("12 of 40 · was 5").
        int? previousCovered = null;

        // workspace-q77e: the layout as it was BEFORE the last refine, so 'z'
        // can undo a tweak that made things worse without losing the
        // almost-perfect version (or starting the whole wizard over).
        SiteHierarchyConfig? undoConfig = null;

        // workspace-r8on: a one-render notice (e.g. an 'x' that was refused because
        // the item shares its only identifier with real stories) so a no-op key
        // press is never silent — the user gets a reason instead of nothing.
        string? notice = null;
        while (!ct.IsCancellationRequested)
        {
            if (IsDegenerate(config, links))
            {
                // Honest failure: no fake-confident preview, no savable empty
                // config. The user can point at a story (one more budget-guarded
                // re-inference) or Esc to leave the page unconfigured.
                var repaired = await RunAdjustAsync(
                    analyzer,
                    input,
                    render,
                    overlay,
                    links,
                    pageUrl,
                    screenshot,
                    proposal,
                    answers,
                    config,
                    budget,
                    freeTextPrompt,
                    pickLeadFromTree,
                    lens,
                    ct,
                    BuildFailureCardShape(config, links, budget),
                    promptLeadUrl).ConfigureAwait(false);
                if (repaired == null)
                {
                    return new Result { Cancelled = true };
                }

                config = repaired.Config;
                confirmQuestion = repaired.ConfirmQuestion;
                continue;
            }

            if (applyPreview != null)
            {
                await applyPreview(config, ct).ConfigureAwait(false);
            }

            // workspace-romy.8: a discriminating confirm question renders as
            // extra answerable rows BELOW the sections. The cursor starts on a
            // section row, so plain Enter still quick-accepts the layout.
            var askable = confirmQuestion != null && IsDiscriminating(confirmQuestion) && !budget.Exhausted
                ? confirmQuestion
                : null;
            var preview = BuildPreviewCard(config, links, previousCovered, askable, canUndo: undoConfig != null, notice: notice);
            notice = null; // transient — shown for exactly one render
            previousCovered = CoveredCount(config, links.Where(l => l.Type == LinkType.Content && !l.IsGroupHeader).ToList());
            var choice = await RunCardAsync(input, render, overlay, preview, lens, ct, adjustable: true).ConfigureAwait(false);
            if (lens != null)
            {
                await lens.ClearAsync(ct).ConfigureAwait(false);
            }

            // workspace-5vqk.3: the layout now renders section headers AND their
            // headline rows, so the confirm-question rows are the LAST
            // askable.Options.Count entries — detect them by that offset, not by
            // config.Sections.Count (which no longer equals the layout-row count).
            var layoutRowCount = preview.Options.Count - (askable?.Options.Count ?? 0);
            if (askable != null && choice >= layoutRowCount)
            {
                // The user answered the confirm question — one more inference
                // round with the answer grounded in its durable identifier.
                var optIndex = Math.Clamp(choice - layoutRowCount, 0, askable.Options.Count - 1);
                var chosenOption = askable.Options[optIndex];
                var identifier = FormatIdentifier(chosenOption.ParentSelector, chosenOption.UrlPattern);
                confirmQuestion = null;
                if (budget.TrySpend())
                {
                    answers.Add(new SetupAnswer
                    {
                        QuestionId = "confirm",
                        Answer = identifier.Length > 0
                            ? $"{askable.Prompt} → {chosenOption.Label} ({identifier})"
                            : $"{askable.Prompt} → {chosenOption.Label}",
                    });

                    // workspace-9k27.4: this path REGENERATES from the proposal
                    // (accepted refines survive only as accumulated answer
                    // text), so keep the current layout undoable — 'z' after a
                    // confirm answer restores what the user was just looking at.
                    undoConfig = config;
                    var inferred = await RunWithProgressAsync(
                        analyzer.InferPatternFromAnswersAsync(screenshot, links, pageUrl, proposal, answers, ct),
                        "Rebuilding your layout",
                        overlay,
                        render,
                        ct).ConfigureAwait(false);
                    config = inferred.Config;
                    confirmQuestion = inferred.ConfirmQuestion;
                }

                continue;
            }

            switch (choice)
            {
                case >= 0: // Enter — save exactly what is previewed
                    return new Result { Config = config };
                case CancelChoice:
                    return new Result { Cancelled = true };
                case UndoChoice:
                    if (undoConfig != null)
                    {
                        config = undoConfig;
                        undoConfig = null;
                        confirmQuestion = null;
                        previousCovered = null;
                    }

                    continue;
                case ExcludeChoice:
                    // workspace-5vqk.4: 'x' on a headline row drops that item and its
                    // token-siblings deterministically — NO model call, NO budget.
                    // workspace-r8on: a header/confirm row or a REFUSED exclusion is a
                    // no-op, but now says WHY instead of silently doing nothing.
                    var rows = BuildPreviewRows(config, links);
                    if (preview.Cursor >= 0 && preview.Cursor < rows.Count && rows[preview.Cursor].Link is { } item)
                    {
                        var pruned = ExcludeItem(config, item, links);
                        if (pruned != null)
                        {
                            undoConfig = config; // 'z' restores the item
                            config = pruned;
                            confirmQuestion = null;
                        }
                        else
                        {
                            notice = $"Can't drop \"{Truncate(CollapseWhitespace(item.DisplayText), 44)}\" on its own — it shares " +
                                     "its only identifier with real stories. Use Space → \"Tell the AI what to change…\" instead.";
                        }
                    }
                    else
                    {
                        notice = "Move to a story row (not a section header), then press x to drop it.";
                    }

                    continue;
                case AdjustChoice:
                    var before = config;
                    var adjusted = await RunAdjustAsync(
                        analyzer,
                        input,
                        render,
                        overlay,
                        links,
                        pageUrl,
                        screenshot,
                        proposal,
                        answers,
                        config,
                        budget,
                        freeTextPrompt,
                        pickLeadFromTree,
                        lens,
                        ct,
                        shape: null,
                        promptLeadUrl: promptLeadUrl).ConfigureAwait(false);
                    if (adjusted != null)
                    {
                        undoConfig = before; // remember the pre-refine layout for 'z'
                        config = adjusted.Config;
                        confirmQuestion = adjusted.ConfirmQuestion;
                    }

                    continue;
            }
        }

        return new Result { Cancelled = true };
    }

    /// <summary>
    /// The adjust loop behind Space on the preview card: point at the main story
    /// (when a tree/click picker is wired) or steer the model with free text.
    /// Each adjustment is exactly one budget-guarded re-inference whose answer is
    /// APPENDED to the running answer list, so adjustments accumulate. Returns
    /// the re-inferred config, or null when the user backed out / the pick was
    /// invalid / the budget is spent (the caller keeps the current config).
    /// </summary>
    private static async Task<InferredPattern?> RunAdjustAsync(
        IHierarchyAnalyzer analyzer,
        IInputHandler input,
        Func<CancellationToken, Task> render,
        SetupWizardOverlay.State overlay,
        List<LinkInfo> links,
        string pageUrl,
        byte[]? screenshot,
        SiteSetupProposal proposal,
        List<SetupAnswer> answers,
        SiteHierarchyConfig currentConfig,
        ModelRoundTripBudget budget,
        Func<CancellationToken, Task<string?>> freeTextPrompt,
        Func<CancellationToken, Task<LinkInfo?>>? pickLeadFromTree,
        Lens? lens,
        CancellationToken ct,
        AdjustCardShape? shape = null,
        Func<CancellationToken, Task<string?>>? promptLeadUrl = null)
    {
        // workspace-romy.7: an exhausted budget gets an honest dead-end card
        // instead of options that would silently no-op after selection.
        if (budget.Exhausted)
        {
            var doneCard = new SetupWizardOverlay.WizardCard
            {
                Title = shape?.Title ?? "Adjust the layout",
                Prompt = "The AI budget for this setup is used up — no more adjustments are possible.",
                Options = new List<SetupWizardOverlay.CardOption>
                {
                    new() { Label = "Back" },
                },
                Footnote = $"{budget.Used} of {budget.Max} AI calls used",
                Hint = "Enter/Esc back",
            };
            await RunCardAsync(input, render, overlay, doneCard, lens, ct).ConfigureAwait(false);
            return null;
        }

        var allowPick = pickLeadFromTree != null;
        var allowUrl = promptLeadUrl != null;

        // workspace-5vqk.6: once the layout already carries a pick-derived river, a
        // further pick adds a SECOND co-equal pattern — say so, so the user knows they
        // can teach podcasts/events as their own sections rather than replacing news.
        var addsAnother = currentConfig.Sections.Any(s => IsPickRiverName(s.Name));
        var options = new List<SetupWizardOverlay.CardOption>();
        var pickIndex = -1;
        var urlIndex = -1;
        if (allowPick)
        {
            pickIndex = options.Count;
            options.Add(new SetupWizardOverlay.CardOption
            {
                Label = addsAnother ? "Pick another story — adds a co-equal section" : "Pick a story to teach the layout",
            });
        }

        // workspace-cbjx.2: let the user name the lead by URL instead of scrolling
        // to it — much easier on a long aggregator list, and a keyboard-only path
        // when the sidecar isn't docked.
        if (allowUrl)
        {
            urlIndex = options.Count;
            options.Add(new SetupWizardOverlay.CardOption
            {
                Label = addsAnother ? "Paste another story's URL — adds a co-equal section" : "Paste a story's URL",
            });
        }

        options.Add(new SetupWizardOverlay.CardOption { Label = "Tell the AI what to change…" });

        // workspace-9k27.4: remember whether this is the degenerate-REPAIR path
        // BEFORE the null-coalescing assignment below fills in the default card
        // shape — the old not-null check ran after that assignment and was
        // therefore always true, which made the RefineLayoutAsync branch DEAD
        // CODE and silently regenerated the layout from scratch on every normal
        // adjustment (the exact defect the q77e incremental-refine work fixed).
        var isRepair = shape != null;

        shape ??= new AdjustCardShape(
            Title: "Adjust the layout",
            Prompt: "What should change?",
            Footnote: budget.Remaining <= 2 ? $"{budget.Remaining} AI call(s) left for this setup" : string.Empty,
            Hint: "↑/↓ choose · Enter select · Esc back to preview");

        var card = new SetupWizardOverlay.WizardCard
        {
            Title = shape.Title,
            Prompt = shape.Prompt,
            Options = options,
            Footnote = shape.Footnote,
            Hint = shape.Hint,
        };

        var choice = await RunCardAsync(input, render, overlay, card, lens, ct).ConfigureAwait(false);
        if (choice < 0)
        {
            return null;
        }

        SetupAnswer? adjustment = null;
        string? instruction = null;
        LinkInfo? resolvedLead = null;
        if (allowPick && choice == pickIndex)
        {
            resolvedLead = await pickLeadFromTree!(ct).ConfigureAwait(false);
            if (resolvedLead != null)
            {
                adjustment = LeadOverrideAnswer(resolvedLead);
                instruction = LeadInstruction(resolvedLead);
            }
        }
        else if (allowUrl && choice == urlIndex)
        {
            var urlText = await promptLeadUrl!(ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(urlText))
            {
                resolvedLead = ResolveLeadByUrl(urlText.Trim(), links);
                if (resolvedLead != null)
                {
                    // Matched a real link: anchor the lead on its durable selector.
                    instruction = LeadInstruction(resolvedLead);
                    adjustment = LeadOverrideAnswer(resolvedLead)
                        ?? new SetupAnswer { QuestionId = "lead-url", Answer = instruction };
                }
                else
                {
                    // No link matched the URL — still steer the model with it.
                    instruction = $"Use the story whose URL is \"{Truncate(urlText.Trim(), 200)}\" as the seed of " +
                                  "the main repeating pattern — extrapolate its sibling stories into one co-equal " +
                                  "section, keeping every other section unchanged.";
                    adjustment = new SetupAnswer { QuestionId = "lead-url", Answer = instruction };
                }
            }
        }
        else
        {
            var freeText = await freeTextPrompt(ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(freeText))
            {
                instruction = freeText.Trim();
                adjustment = new SetupAnswer { QuestionId = "adjustment", Answer = instruction };
            }
        }

        // workspace-zd96: when the lead resolves to a real page link (URL or tree
        // pick), derive the story river straight from the DOM — deterministically,
        // with NO model round-trip and NO budget spend. This is what makes "point
        // at the main story / paste its URL" reliable on aggregators: the old path
        // handed a text hint to the model, which on Techmeme returned a Top Story
        // matching ZERO links and ad-shaped sections. The derived river matches the
        // lead by construction, so a 0-link section is impossible.
        if (resolvedLead != null)
        {
            var derived = BuildDeterministicLeadOverride(resolvedLead, links, currentConfig);
            if (derived != null)
            {
                return derived;
            }
        }

        if (adjustment == null || instruction == null || !budget.TrySpend())
        {
            return null;
        }

        // workspace-q77e: the failure/degenerate-repair path has no good layout
        // to preserve, so it re-derives from the proposal + accumulated answers
        // as before. The normal preview "adjust" REFINES the layout the user is
        // looking at — the model edits the current config and keeps the working
        // parts, instead of regenerating from scratch (which could throw away an
        // almost-perfect layout over one tweak).
        if (isRepair)
        {
            answers.Add(adjustment);
            var rebuilt = await RunWithProgressAsync(
                analyzer.InferPatternFromAnswersAsync(screenshot, links, pageUrl, proposal, answers, ct),
                "Rebuilding your layout",
                overlay,
                render,
                ct).ConfigureAwait(false);
            return PruneEmptySections(rebuilt, links);
        }

        answers.Add(adjustment);
        var refined = await RunWithProgressAsync(
            analyzer.RefineLayoutAsync(screenshot, links, pageUrl, currentConfig, instruction, ct),
            "Applying your change",
            overlay,
            render,
            ct).ConfigureAwait(false);
        return PruneEmptySections(refined, links);
    }

    /// <summary>
    /// workspace-zd96: builds the lead-override config straight from the DOM —
    /// a single durable "Top stories" river section derived by
    /// <see cref="LeadOverrideDerivation"/> — so the resolved lead and its sibling
    /// stories are captured without a model call, and a 0-link section is
    /// impossible. Returns null when nothing generalizes (caller falls back).
    /// </summary>
    private static InferredPattern? BuildDeterministicLeadOverride(
        LinkInfo lead, List<LinkInfo> links, SiteHierarchyConfig currentConfig)
    {
        var deriv = LeadOverrideDerivation.Derive(lead, links);
        if (deriv is null || deriv.StoryMatchCount <= 0)
        {
            return null;
        }

        var contentLinks = links.Where(l => l.Type == LinkType.Content && !l.IsGroupHeader).ToList();

        // workspace-5vqk.2/.6: a pick teaches ONE repeating pattern — a co-equal river
        // of sibling stories, NOT a single lead. The FIRST pick names its river
        // "Stories" and LEADS (SortOrder 0), merging with the model's other real
        // sections instead of blanket-replacing them. A SECOND/THIRD pick (the config
        // already carries a "Stories" river) defines an ADDITIONAL co-equal section:
        // it is APPENDED (SortOrder by pick order) with a distinct name, so a
        // genuinely multi-pattern page (news + podcasts + events) is representable
        // without electing one lead. Never MaxLinks; never "Top stories".
        var priorPicks = currentConfig.Sections.Count(s => IsPickRiverName(s.Name));
        var isFirstPick = priorPicks == 0;
        var river = new HierarchySection
        {
            Name = isFirstPick ? "Stories" : $"Stories {priorPicks + 1}",
            SortOrder = 0,
            ParentSelectors = deriv.RiverSelectors,
        };

        // The deterministic shortcut is only trustworthy when the PICK actually
        // EXTRAPOLATED — the river must be sound on its OWN links, not merely rescued
        // by the sections we are about to merge in. A pick that generalizes to almost
        // nothing (its selector doesn't repeat) falls back to the model instead of
        // saving a 1-story "Stories" section — the very "one story" failure we fight.
        var riverOnly = currentConfig with { Sections = new List<HierarchySection> { river } };
        if (IsDegenerate(riverOnly, links))
        {
            return null;
        }

        var riverLinks = contentLinks.Where(l => NavigationTreeBuilder.MatchesSection(l, river)).ToHashSet();

        // An additional pick that covers nothing the current layout doesn't already
        // show is a redundant re-pick — return the layout unchanged (no duplicate
        // section, no model call) instead of stacking an overlapping "Stories 2".
        if (!isFirstPick && riverLinks.All(l => currentConfig.Sections.Any(sec => NavigationTreeBuilder.MatchesSection(l, sec))))
        {
            return new InferredPattern { Config = currentConfig, Confidence = 1.0 };
        }

        // Preserve every prior section that still covers a story the river does NOT
        // (a genuinely distinct co-equal cluster); drop empty/subsumed ones so the
        // merge never surfaces a duplicate or a 0-link row.
        var preserved = currentConfig.Sections
            .Where(sec => contentLinks.Any(l => !riverLinks.Contains(l) && NavigationTreeBuilder.MatchesSection(l, sec)))
            .ToList();

        // First pick leads the page; an additional pick appends AFTER the existing
        // sections (co-equal, SortOrder by pick order) so the first-taught river stays on top.
        var sections = isFirstPick
            ? new List<HierarchySection> { river }.Concat(preserved).ToList()
            : preserved.Concat(new[] { river }).ToList();
        for (var i = 0; i < sections.Count; i++)
        {
            sections[i] = sections[i] with { SortOrder = i };
        }

        var config = currentConfig with
        {
            Sections = sections,
            ExcludeSelectors = currentConfig.ExcludeSelectors
                .Concat(deriv.ExcludeSelectors)
                .Distinct(StringComparer.Ordinal)
                .ToList(),
            ModelVersion = string.IsNullOrEmpty(currentConfig.ModelVersion)
                ? "lead-derived"
                : currentConfig.ModelVersion + "+lead-derived",
            NeedsReanalyze = false,
        };

        // Only take the deterministic shortcut when it actually produces a sound
        // layout over THIS page's links. If the river matches (almost) nothing —
        // e.g. the lead's selector doesn't generalize — fall back to the model
        // instead of handing the preview loop a degenerate config it would try to
        // repair forever.
        if (IsDegenerate(config, links))
        {
            return null;
        }

        return new InferredPattern { Config = config, Confidence = 1.0 };
    }

    /// <summary>
    /// workspace-zd96: drops any section that matches no Content link on the page,
    /// so a model refine can never surface a "Top Story — 0 links" row. A config
    /// where EVERY section is empty is left intact for the degenerate gate to
    /// handle honestly. Null passes through.
    /// </summary>
    private static InferredPattern? PruneEmptySections(InferredPattern? pattern, List<LinkInfo> links)
    {
        if (pattern is null)
        {
            return null;
        }

        var contentLinks = links.Where(l => l.Type == LinkType.Content && !l.IsGroupHeader).ToList();
        var nonEmpty = pattern.Config.Sections
            .Where(sec => contentLinks.Any(l => NavigationTreeBuilder.MatchesSection(l, sec)))
            .ToList();

        if (nonEmpty.Count == pattern.Config.Sections.Count || nonEmpty.Count == 0)
        {
            return pattern;
        }

        return pattern with { Config = pattern.Config with { Sections = nonEmpty } };
    }

    private static string EscapeAttrValue(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    /// <summary>workspace-5vqk.3: collapses whitespace runs (headline text may carry DOM newlines/indent).</summary>
    private static string CollapseWhitespace(string text) =>
        string.Join(' ', (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>workspace-5vqk.6: true for a section name a deterministic pick produced ("Stories", "Stories 2", …).</summary>
    private static bool IsPickRiverName(string name) =>
        string.Equals(name, "Stories", StringComparison.Ordinal)
        || name.StartsWith("Stories ", StringComparison.Ordinal);

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";

    /// <summary>workspace-5vqk.2: the refine instruction that seeds the repeating story pattern from <paramref name="lead"/>.</summary>
    private static string LeadInstruction(LinkInfo lead) =>
        $"Use this story as the SEED of the main repeating pattern — extrapolate its sibling " +
        $"stories into one co-equal section, keeping every other section " +
        $"unchanged: \"{Truncate(lead.DisplayText, 90)}\"" +
        (string.IsNullOrWhiteSpace(lead.ParentSelector) ? string.Empty : $" (parent {lead.ParentSelector}).");

    /// <summary>
    /// The one card input loop: ↑/↓ cycles options (highlighting each on the
    /// lens), Enter returns the cursor, Esc returns <see cref="CancelChoice"/>.
    /// When <paramref name="adjustable"/> (the preview card), Space returns
    /// <see cref="AdjustChoice"/>.
    /// </summary>
    private static async Task<int> RunCardAsync(
        IInputHandler input,
        Func<CancellationToken, Task> render,
        SetupWizardOverlay.State overlay,
        SetupWizardOverlay.WizardCard card,
        Lens? lens,
        CancellationToken ct,
        bool adjustable = false)
    {
        overlay.Mode = SetupWizardOverlay.Mode.Card;
        overlay.Card = card;
        await render(ct).ConfigureAwait(false);
        await PreviewFocusedOptionAsync(lens, card, ct).ConfigureAwait(false);

        var count = Math.Max(1, card.Options.Count);
        while (!ct.IsCancellationRequested)
        {
            var command = await input.WaitForInputAsync(ct).ConfigureAwait(false);

            // workspace-5vqk.4: 'x' on the preview drops the focused item. It has no
            // dedicated CommandType, so match the raw key (as the schedule picker does).
            if (adjustable && command.RawKeyChar is 'x' or 'X')
            {
                return ExcludeChoice;
            }

            switch (command.Type)
            {
                case CommandType.MoveDown or CommandType.ExpandNode:
                    card.Cursor = (card.Cursor + 1) % count;
                    await render(ct).ConfigureAwait(false);
                    await PreviewFocusedOptionAsync(lens, card, ct).ConfigureAwait(false);
                    break;
                case CommandType.MoveUp or CommandType.CollapseNode:
                    card.Cursor = (card.Cursor - 1 + count) % count;
                    await render(ct).ConfigureAwait(false);
                    await PreviewFocusedOptionAsync(lens, card, ct).ConfigureAwait(false);
                    break;
                case CommandType.ActivateLink:
                    return card.Cursor;
                case CommandType.ToggleSelection when adjustable:
                    return AdjustChoice;
                case CommandType.Undo when adjustable:
                    return UndoChoice;
                case CommandType.GoBack or CommandType.Quit:
                    return CancelChoice;
                case CommandType.TerminalResized:
                    await render(ct).ConfigureAwait(false);
                    break;
            }
        }

        return CancelChoice;
    }

    /// <summary>
    /// workspace-wylw: highlights the focused option's matches on the sidecar
    /// lens (clears when the option carries no selector). Advisory only — the
    /// card still shows the durable identifier as text; lens failures never
    /// interrupt the wizard.
    /// </summary>
    private static async Task PreviewFocusedOptionAsync(
        Lens? lens, SetupWizardOverlay.WizardCard card, CancellationToken ct)
    {
        if (lens == null || card.Options.Count == 0)
        {
            return;
        }

        try
        {
            var selector = card.Options[Math.Clamp(card.Cursor, 0, card.Options.Count - 1)].HighlightSelector;
            if (string.IsNullOrEmpty(selector))
            {
                await lens.ClearAsync(ct).ConfigureAwait(false);
            }
            else
            {
                await lens.HighlightCssAsync(selector, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Lens preview is cosmetic.
        }
    }

    /// <summary>
    /// workspace-5oe9.13: awaits a model round-trip while keeping the "Analyzing…"
    /// card live via a caller-tick await-pump — the spinner advances and an
    /// elapsed-seconds label updates every ~250ms while the call is pending, and
    /// the box stops the instant the call returns (no background timer).
    /// </summary>
    private static async Task<T> RunWithProgressAsync<T>(
        Task<T> modelCall,
        string label,
        SetupWizardOverlay.State overlay,
        Func<CancellationToken, Task> render,
        CancellationToken ct)
    {
        overlay.Mode = SetupWizardOverlay.Mode.Analyzing;
        overlay.AnalyzingLabel = $"{label}…";
        await render(ct).ConfigureAwait(false);

        return await ProgressPump.RunAsync(
            modelCall,
            async elapsed =>
            {
                overlay.SpinnerFrame++;
                overlay.AnalyzingLabel = $"{label}… {elapsed.TotalSeconds:0}s";
                await render(ct).ConfigureAwait(false);
            },
            ProgressPump.DefaultInterval,
            ct).ConfigureAwait(false);
    }

    private static SetupWizardOverlay.WizardCard BuildQuestionCard(SetupQuestion question, int number, int total)
    {
        // workspace-6yb7.4: IsDiscriminating guarantees >= 2 concrete options, so
        // there is no synthesized yes/no fallback — every row is a real
        // alternative whose matches light up on the lens as it is focused.
        var options = question.Options.Select(o => new SetupWizardOverlay.CardOption
        {
            Label = o.Label,
            Identifier = FormatIdentifier(o.ParentSelector, o.UrlPattern),
            HighlightSelector = CssForIdentifier(o.ParentSelector, o.UrlPattern),
        }).ToList();
        var defaultCursor = Math.Max(0, options.FindIndex(o =>
            string.Equals(o.Label, question.DefaultAnswer, StringComparison.OrdinalIgnoreCase)));

        return new SetupWizardOverlay.WizardCard
        {
            Title = $"Set up this site with AI · {number} of {total}",
            Prompt = question.Prompt,
            Options = options,
            Cursor = defaultCursor,
            Hint = "↑/↓ see each option on the page · Enter choose · Esc cancel",
        };
    }

    private static string FormatIdentifier(string parentSelector, string urlPattern)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(parentSelector))
        {
            parts.Add(parentSelector);
        }

        if (!string.IsNullOrWhiteSpace(urlPattern))
        {
            parts.Add(urlPattern);
        }

        return string.Join(" · ", parts);
    }

    /// <summary>
    /// The shape (title/prompt/footnote/hint) RunAdjustAsync dresses its card
    /// in — the same loop serves both the preview's Space-adjust and the
    /// degenerate gate's honest failure card (workspace-6yb7.6).
    /// </summary>
    internal sealed record AdjustCardShape(string Title, string Prompt, string Footnote, string Hint);

    /// <summary>
    /// workspace-wylw: best-effort hooks into the sidecar lens tab.
    /// <c>HighlightCssAsync</c> evaluates <see cref="TunerScript.Highlight"/>
    /// (dialect css) and returns the match count (-1 for an invalid selector);
    /// <c>ClearAsync</c> removes every highlight. Null where no lens exists.
    /// </summary>
    internal sealed record Lens(
        Func<string, CancellationToken, Task<int>> HighlightCssAsync,
        Func<CancellationToken, Task> ClearAsync);

    internal sealed record Result
    {
        public SiteHierarchyConfig? Config { get; init; }

        public bool Cancelled { get; init; }
    }

    /// <summary>workspace-5vqk.3/.4: one preview row — its rendered option plus the link it stands for (null for a section header).</summary>
    internal sealed record PreviewRow(SetupWizardOverlay.CardOption Option, LinkInfo? Link);
}
