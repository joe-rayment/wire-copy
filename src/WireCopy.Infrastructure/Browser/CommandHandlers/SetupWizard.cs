// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Browser.UI.Components;

namespace WireCopy.Infrastructure.Browser.CommandHandlers;

/// <summary>
/// workspace-5oe9.8 — the question-driven AI setup wizard (CENTERPIECE B, UI).
/// Calls <see cref="IHierarchyAnalyzer.ProposeSetupQuestionsAsync"/> (round 1),
/// renders the proposed pattern + a BOUNDED set of confirm-the-pattern cards
/// (each showing the DURABLE identifier that will be saved), collects the user's
/// answers keyboard-only, then calls
/// <see cref="IHierarchyAnalyzer.InferPatternFromAnswersAsync"/> (round 2) to
/// produce the final durable config. The accept-all path is "Enter, Enter,
/// Enter" and spends exactly two model round-trips.
/// </summary>
internal static class SetupWizard
{
    /// <summary>Cap on structured question cards shown (a final optional free-text card may follow).</summary>
    internal const int MaxStructuredQuestions = 3;

    /// <summary>
    /// Runs the wizard. Dependencies are injected (not pulled from CommandContext)
    /// so the flow is unit-testable with a mock analyzer + scripted input.
    /// </summary>
    /// <param name="render">Repaints the page + overlay (the overlay painter reads <paramref name="overlay"/>).</param>
    /// <param name="freeTextPrompt">Prompts the optional final free-text card; returns null/empty to skip.</param>
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

        // ---- Overview card: confirm the proposed pattern (or bail to doc order,
        // or point at the main story yourself when a tree picker is wired) ----
        var overview = BuildOverviewCard(proposal, allowPick: pickLeadFromTree != null);
        var ovChoice = await RunCardAsync(input, render, overlay, overview, lens, ct).ConfigureAwait(false);
        if (ovChoice < 0)
        {
            return new Result { Cancelled = true };
        }

        if (ovChoice == 1)
        {
            // "Use plain document order instead"
            return new Result { UseDocumentOrder = true };
        }

        // workspace-5oe9.9: "Let me point at the main story" — the user highlights
        // a real link in the previewed tree; we re-infer once with that override.
        LinkInfo? pickedLead = null;
        if (ovChoice == 2 && pickLeadFromTree != null)
        {
            pickedLead = await pickLeadFromTree(ct).ConfigureAwait(false);
        }

        // ---- Up to MaxStructuredQuestions confirm-the-pattern cards ----
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

        // ---- Optional free-text card ----
        var freeText = await freeTextPrompt(ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(freeText))
        {
            answers.Add(new SetupAnswer { QuestionId = "freeform", Answer = freeText.Trim() });
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

        // workspace-5oe9.9: apply a point-at-a-link lead override with exactly
        // one extra re-inference (budget-guarded). Header/synthetic picks are
        // rejected by RefineWithPickedLeadAsync and leave the base config.
        if (pickedLead != null)
        {
            var refined = await RefineWithPickedLeadAsync(
                analyzer, screenshot, links, pageUrl, proposal, answers, pickedLead, budget, ct).ConfigureAwait(false);
            if (refined != null)
            {
                config = refined;
            }
        }

        // ---- workspace-wylw: live confirmation — every proposed section lights
        // up its matched story links on the lens; Enter saves, Esc backs out. ----
        if (config.Sections.Count > 0)
        {
            var confirm = BuildConfirmSectionsCard(config, links);
            var saveChoice = await RunCardAsync(input, render, overlay, confirm, lens, ct).ConfigureAwait(false);
            if (lens != null)
            {
                await lens.ClearAsync(ct).ConfigureAwait(false);
            }

            if (saveChoice < 0)
            {
                return new Result { Cancelled = true };
            }
        }

        return new Result { Config = config };
    }

    /// <summary>
    /// workspace-wylw: the final confirmation card — one row per durable section
    /// with its REAL match count against the current page's links (the same
    /// <see cref="NavigationTreeBuilder.MatchesSection"/> semantics revisits use),
    /// so a degenerate config is visible before it is saved. Focusing a row
    /// highlights all of its matched links on the sidecar lens.
    /// </summary>
    internal static SetupWizardOverlay.WizardCard BuildConfirmSectionsCard(
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
            ? "⚠ No links on this page match — Esc and try document order instead"
            : $"{covered} of {contentLinks.Count} story links covered";

        return new SetupWizardOverlay.WizardCard
        {
            Title = "Confirm your story list",
            Prompt = "The live page highlights each section's stories — do they look right?",
            Options = options,
            Footnote = footnote,
            Hint = "↑/↓ preview a section · Enter save · Esc cancel",
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
    /// workspace-5oe9.9: re-infers the durable config with the user's
    /// point-at-a-link lead override appended as an answer — the single permitted
    /// extra round-trip (guarded by <paramref name="budget"/>). Returns null when
    /// the pick is invalid (header / no identifier) or the budget is exhausted,
    /// so the caller keeps the base config.
    /// </summary>
    internal static async Task<SiteHierarchyConfig?> RefineWithPickedLeadAsync(
        IHierarchyAnalyzer analyzer,
        byte[]? screenshot,
        List<LinkInfo> links,
        string pageUrl,
        SiteSetupProposal proposal,
        IReadOnlyList<SetupAnswer> answers,
        LinkInfo pickedLead,
        ModelRoundTripBudget budget,
        CancellationToken ct)
    {
        var section = SectionFromPickedLink(pickedLead, "Top Story");
        if (section == null)
        {
            return null;
        }

        if (!budget.TrySpend())
        {
            return null;
        }

        var identifier = string.Join(" · ", section.ParentSelectors.Concat(section.UrlPatterns));
        var refined = answers.Append(new SetupAnswer
        {
            QuestionId = "lead-override",
            Answer = $"The single main/lead story is the link matching {identifier} (\"{pickedLead.DisplayText}\"). " +
                     "Put it alone in the Top Story section.",
        }).ToList();

        return await analyzer.InferPatternFromAnswersAsync(
            screenshot, links, pageUrl, proposal, refined, ct).ConfigureAwait(false);
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

    private static SetupWizardOverlay.WizardCard BuildOverviewCard(SiteSetupProposal proposal, bool allowPick)
    {
        var top = proposal.ProposedPattern.TopStory;
        var tierCount = proposal.ProposedPattern.Tiers.Count;
        var excludeCount = proposal.ProposedPattern.Exclude.Count;

        var footnote = $"{tierCount} story tier(s) · {excludeCount} group(s) to hide";

        var options = new List<SetupWizardOverlay.CardOption>
        {
            new()
            {
                Label = top != null ? $"Looks good — main story: \"{Truncate(top.Label, 36)}\"" : "Looks good — set it up",
                Identifier = top != null ? FormatIdentifier(top.ParentSelector, top.UrlPattern) : string.Empty,
                HighlightSelector = top != null ? CssForIdentifier(top.ParentSelector, top.UrlPattern) : string.Empty,
            },
            new() { Label = "Use plain document order instead" },
        };

        // Index 2 (only when a tree picker is wired): point at the main story.
        if (allowPick)
        {
            options.Add(new SetupWizardOverlay.CardOption { Label = "Let me point at the main story" });
        }

        return new SetupWizardOverlay.WizardCard
        {
            Title = "Set up this site with AI",
            Prompt = "Here's the layout I inferred — does it look right?",
            Options = options,
            Footnote = footnote,
            Hint = "↑/↓ choose · Enter confirm · Esc cancel",
        };
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

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";

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

        public bool UseDocumentOrder { get; init; }

        public bool Cancelled { get; init; }
    }
}
