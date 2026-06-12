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

    /// <summary>Card-loop sentinel: Esc/quit (a chosen option is &gt;= 0).</summary>
    private const int CancelChoice = -1;

    /// <summary>Card-loop sentinel: Space on an adjustable card (the preview).</summary>
    private const int AdjustChoice = -2;

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
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(analyzer);
        ArgumentNullException.ThrowIfNull(budget);

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

        // ---- Preview loop: show the RESULT, not a description of it ----
        // workspace-romy.7: previousCovered carries the last previewed config's
        // coverage into the next preview so each adjustment round shows what
        // changed ("12 of 40 · was 5").
        int? previousCovered = null;
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
                    budget,
                    freeTextPrompt,
                    pickLeadFromTree,
                    lens,
                    ct,
                    BuildFailureCardShape(config, links, budget)).ConfigureAwait(false);
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
            var preview = BuildPreviewCard(config, links, previousCovered, askable);
            previousCovered = Coverage(config, links).Covered;
            var choice = await RunCardAsync(input, render, overlay, preview, lens, ct, adjustable: true).ConfigureAwait(false);
            if (lens != null)
            {
                await lens.ClearAsync(ct).ConfigureAwait(false);
            }

            if (askable != null && choice >= config.Sections.Count)
            {
                // The user answered the confirm question — one more inference
                // round with the answer grounded in its durable identifier.
                var optIndex = Math.Clamp(choice - config.Sections.Count, 0, askable.Options.Count - 1);
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
                    inferred = await RunWithProgressAsync(
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
                case AdjustChoice:
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
                        budget,
                        freeTextPrompt,
                        pickLeadFromTree,
                        lens,
                        ct).ConfigureAwait(false);
                    if (adjusted != null)
                    {
                        config = adjusted.Config;
                        confirmQuestion = adjusted.ConfirmQuestion;
                    }

                    continue;
            }
        }

        return new Result { Cancelled = true };
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
                             "page's main stories — broaden the section identifiers so the main story list is covered.",
                };
            }
        }

        return null;
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
        SetupQuestion? confirmQuestion = null)
    {
        var contentLinks = links.Where(l => l.Type == LinkType.Content && !l.IsGroupHeader).ToList();
        var options = config.Sections.Select(section => new SetupWizardOverlay.CardOption
        {
            Label = $"{section.Name} — {contentLinks.Count(l => NavigationTreeBuilder.MatchesSection(l, section))} link(s)",
            Identifier = string.Join(" · ", section.ParentSelectors.Concat(section.UrlPatterns)),
            HighlightSelector = CssForSection(section),
        }).ToList();

        // workspace-romy.8: the confirm question's options render as answerable
        // rows after the sections; the prompt line carries the question. The
        // cursor stays on row 0 (a section), so Enter still saves as-is.
        if (confirmQuestion != null)
        {
            options.AddRange(confirmQuestion.Options.Select(o => new SetupWizardOverlay.CardOption
            {
                Label = $"AI asks · {o.Label}",
                Identifier = FormatIdentifier(o.ParentSelector, o.UrlPattern),
                HighlightSelector = CssForIdentifier(o.ParentSelector, o.UrlPattern),
            }));
        }

        var covered = contentLinks.Count(l => config.Sections.Any(sec => NavigationTreeBuilder.MatchesSection(l, sec)));
        var footnote = covered == 0 && contentLinks.Count > 0
            ? "⚠ No links on this page match this layout"
            : $"{covered} of {contentLinks.Count} story links covered";

        // workspace-romy.7: after an adjustment, show the coverage delta so the
        // user can see whether the round actually changed anything.
        if (previousCovered is { } prev && prev != covered && contentLinks.Count > 0)
        {
            footnote += $" · was {prev}";
        }

        return new SetupWizardOverlay.WizardCard
        {
            Title = "Your new layout",
            Prompt = confirmQuestion != null
                ? $"AI is unsure: {confirmQuestion.Prompt} Answer below — or save as shown."
                : "The page now shows the layout that will be saved.",
            Options = options,
            Footnote = footnote,
            Hint = "↑/↓ preview a section · Enter save · Space adjust · Esc discard",
        };
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
    /// workspace-6yb7.6: the honest failure card shown instead of a preview
    /// when the config (still) matches ~none of the page's story links.
    /// </summary>
    internal static AdjustCardShape BuildFailureCardShape(
        SiteHierarchyConfig config, List<LinkInfo> links, ModelRoundTripBudget budget)
    {
        var (covered, total) = Coverage(config, links);
        return new AdjustCardShape(
            Title: "No reliable pattern found",
            Prompt: "I couldn't find a layout that matches this page's stories.",
            Footnote: budget.Exhausted
                ? $"Matches {covered} of {total} story links · AI budget used up — Esc to leave unconfigured"
                : $"The proposed layout matches {covered} of {total} story links on this page",
            Hint: "↑/↓ choose · Enter select · Esc leave unconfigured");
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
        ModelRoundTripBudget budget,
        Func<CancellationToken, Task<string?>> freeTextPrompt,
        Func<CancellationToken, Task<LinkInfo?>>? pickLeadFromTree,
        Lens? lens,
        CancellationToken ct,
        AdjustCardShape? shape = null)
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
        var options = new List<SetupWizardOverlay.CardOption>();
        if (allowPick)
        {
            options.Add(new SetupWizardOverlay.CardOption { Label = "Point at the main story" });
        }

        options.Add(new SetupWizardOverlay.CardOption { Label = "Tell the AI what to change…" });

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
        if (allowPick && choice == 0)
        {
            var picked = await pickLeadFromTree!(ct).ConfigureAwait(false);
            adjustment = picked != null ? LeadOverrideAnswer(picked) : null;
        }
        else
        {
            var freeText = await freeTextPrompt(ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(freeText))
            {
                adjustment = new SetupAnswer { QuestionId = "adjustment", Answer = freeText.Trim() };
            }
        }

        if (adjustment == null || !budget.TrySpend())
        {
            return null;
        }

        answers.Add(adjustment);
        return await RunWithProgressAsync(
            analyzer.InferPatternFromAnswersAsync(screenshot, links, pageUrl, proposal, answers, ct),
            "Rebuilding your layout",
            overlay,
            render,
            ct).ConfigureAwait(false);
    }

    private static string EscapeAttrValue(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";

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
}
