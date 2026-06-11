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

    /// <summary>Outcome of one pass through the preview card.</summary>
    private enum PreviewChoice
    {
        Save,
        Adjust,
        Cancel,
    }

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
        var answers = new List<SetupAnswer>();
        var questions = proposal.Questions.Take(MaxStructuredQuestions).ToList();
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

        var config = await RunWithProgressAsync(
            analyzer.InferPatternFromAnswersAsync(screenshot, links, pageUrl, proposal, answers, ct),
            "Building your layout",
            overlay,
            render,
            ct).ConfigureAwait(false);

        // ---- Preview loop: show the RESULT, not a description of it ----
        while (!ct.IsCancellationRequested)
        {
            if (config.Sections.Count == 0)
            {
                // Degenerate output — handled by the gate (workspace-6yb7.6).
                return new Result { Config = config };
            }

            if (applyPreview != null)
            {
                await applyPreview(config, ct).ConfigureAwait(false);
            }

            var preview = BuildPreviewCard(config, links);
            var choice = await RunPreviewCardAsync(input, render, overlay, preview, lens, ct).ConfigureAwait(false);
            if (lens != null)
            {
                await lens.ClearAsync(ct).ConfigureAwait(false);
            }

            switch (choice)
            {
                case PreviewChoice.Save:
                    return new Result { Config = config };
                case PreviewChoice.Cancel:
                    return new Result { Cancelled = true };
                case PreviewChoice.Adjust:
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
                        config = adjusted;
                    }

                    continue;
            }
        }

        return new Result { Cancelled = true };
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
        SiteHierarchyConfig config, List<LinkInfo> links)
    {
        var contentLinks = links.Where(l => l.Type == LinkType.Content && !l.IsGroupHeader).ToList();
        var options = config.Sections.Select(section => new SetupWizardOverlay.CardOption
        {
            Label = $"{section.Name} — {contentLinks.Count(l => NavigationTreeBuilder.MatchesSection(l, section))} link(s)",
            Identifier = string.Join(" · ", section.ParentSelectors.Concat(section.UrlPatterns)),
            HighlightSelector = CssForSection(section),
        }).ToList();

        var covered = contentLinks.Count(l => config.Sections.Any(sec => NavigationTreeBuilder.MatchesSection(l, sec)));
        var footnote = covered == 0 && contentLinks.Count > 0
            ? "⚠ No links on this page match this layout"
            : $"{covered} of {contentLinks.Count} story links covered";

        return new SetupWizardOverlay.WizardCard
        {
            Title = "Your new layout",
            Prompt = "The page now shows the layout that will be saved.",
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
    /// The adjust loop behind Space on the preview card: point at the main story
    /// (when a tree/click picker is wired) or steer the model with free text.
    /// Each adjustment is exactly one budget-guarded re-inference whose answer is
    /// APPENDED to the running answer list, so adjustments accumulate. Returns
    /// the re-inferred config, or null when the user backed out / the pick was
    /// invalid / the budget is spent (the caller keeps the current config).
    /// </summary>
    private static async Task<SiteHierarchyConfig?> RunAdjustAsync(
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
        CancellationToken ct)
    {
        var allowPick = pickLeadFromTree != null;
        var options = new List<SetupWizardOverlay.CardOption>();
        if (allowPick)
        {
            options.Add(new SetupWizardOverlay.CardOption { Label = "Point at the main story" });
        }

        options.Add(new SetupWizardOverlay.CardOption { Label = "Tell the AI what to change…" });

        var card = new SetupWizardOverlay.WizardCard
        {
            Title = "Adjust the layout",
            Prompt = "What should change?",
            Options = options,
            Footnote = budget.Exhausted ? "AI budget for this setup is used up — Esc and save or discard" : string.Empty,
            Hint = "↑/↓ choose · Enter select · Esc back to preview",
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

    private static async Task<int> RunCardAsync(
        IInputHandler input,
        Func<CancellationToken, Task> render,
        SetupWizardOverlay.State overlay,
        SetupWizardOverlay.WizardCard card,
        Lens? lens,
        CancellationToken ct)
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
                case CommandType.GoBack or CommandType.Quit:
                    return -1;
                case CommandType.TerminalResized:
                    await render(ct).ConfigureAwait(false);
                    break;
            }
        }

        return -1;
    }

    /// <summary>
    /// The preview card's input loop: ↑/↓ cycles section highlights on the lens,
    /// Enter saves, Space opens the adjust loop, Esc discards.
    /// </summary>
    private static async Task<PreviewChoice> RunPreviewCardAsync(
        IInputHandler input,
        Func<CancellationToken, Task> render,
        SetupWizardOverlay.State overlay,
        SetupWizardOverlay.WizardCard card,
        Lens? lens,
        CancellationToken ct)
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
                    return PreviewChoice.Save;
                case CommandType.ToggleSelection:
                    return PreviewChoice.Adjust;
                case CommandType.GoBack or CommandType.Quit:
                    return PreviewChoice.Cancel;
                case CommandType.TerminalResized:
                    await render(ct).ConfigureAwait(false);
                    break;
            }
        }

        return PreviewChoice.Cancel;
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
        List<SetupWizardOverlay.CardOption> options;
        int defaultCursor;

        if (question.Options.Count > 0)
        {
            options = question.Options.Select(o => new SetupWizardOverlay.CardOption
            {
                Label = o.Label,
                Identifier = FormatIdentifier(o.ParentSelector, o.UrlPattern),
                HighlightSelector = CssForIdentifier(o.ParentSelector, o.UrlPattern),
            }).ToList();
            defaultCursor = Math.Max(0, options.FindIndex(o =>
                string.Equals(o.Label, question.DefaultAnswer, StringComparison.OrdinalIgnoreCase)));
        }
        else
        {
            // No options → a yes/no confirmation seeded from the default answer.
            var yes = string.IsNullOrWhiteSpace(question.DefaultAnswer) ? "Yes" : question.DefaultAnswer;
            options = new List<SetupWizardOverlay.CardOption>
            {
                new() { Label = yes },
                new() { Label = "No" },
            };
            defaultCursor = 0;
        }

        return new SetupWizardOverlay.WizardCard
        {
            Title = $"Set up this site with AI · {number} of {total}",
            Prompt = question.Prompt,
            Options = options,
            Cursor = defaultCursor,
            Hint = "↑/↓ choose · Enter accept · Esc cancel",
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
