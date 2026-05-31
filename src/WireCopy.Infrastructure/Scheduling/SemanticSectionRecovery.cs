// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Extensions.Logging;
using WireCopy.Application.DTOs.Scheduling;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Application.Interfaces.Scheduling;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.Enums.Scheduling;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Domain.ValueObjects.Scheduling;
using WireCopy.Infrastructure.Browser;

namespace WireCopy.Infrastructure.Scheduling;

/// <summary>
/// workspace-frpl.10 (B9b) — the budgeted, confidence-gated, self-tested semantic
/// recovery tier. It is reached only when the durable section matched 0 AND B9a's
/// deterministic re-derivation also failed. It asks the model (ONE round-trip) which
/// heading present today corresponds to the wanted section, then ACCEPTS the answer
/// only when ALL of these hold: the model returned a single candidate that is one of
/// the offered headings; its confidence clears <see cref="MinConfidence"/>; and a
/// self-test (the durable matcher re-matches ≥1 content article under that heading)
/// passes. Otherwise it returns null and the run falls through to a loud skip. The
/// recovery is run-LOCAL only — it is never written back to the shared config; the
/// schedules screen (B11) asks the user to ratify it. Model calls are capped per day
/// to bound cost, mirroring <see cref="ModelRoundTripBudget"/>'s "enforced, not just
/// documented" discipline.
/// </summary>
internal sealed class SemanticSectionRecovery : ISemanticSectionRecovery
{
    /// <summary>A single ambiguous guess is never auto-accepted; only a confident one.</summary>
    private const double MinConfidence = 0.80;

    /// <summary>Hard ceiling on recovery model calls per local day (cost guard).</summary>
    private const int MaxCallsPerDay = 20;

    private readonly IHierarchyAnalyzer _analyzer;
    private readonly ILogger<SemanticSectionRecovery> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly object _budgetLock = new();
    private DateOnly _budgetDay;
    private int _callsToday;

    public SemanticSectionRecovery(
        IHierarchyAnalyzer analyzer,
        ILogger<SemanticSectionRecovery> logger,
        TimeProvider? timeProvider = null)
    {
        _analyzer = analyzer;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<SectionResolution?> TryRecoverAsync(
        SiteHierarchyConfig config,
        IReadOnlyList<LinkInfo> todaysLinks,
        RecipeStep step,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(todaysLinks);
        ArgumentNullException.ThrowIfNull(step);

        if (!_analyzer.IsConfigured)
        {
            return null; // no model available — stay with the loud skip
        }

        // Same content-link filtering the resolver uses, so an ad/promo can never be
        // offered as a candidate heading or counted as a recovered article.
        var content = todaysLinks
            .Where(l => l.Type == LinkType.Content && !l.IsGroupHeader && !NavigationTreeBuilder.IsExcluded(l, config))
            .ToList();

        var candidateLabels = content
            .Select(l => l.SectionTitle)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (candidateLabels.Count == 0)
        {
            return null; // nothing semantically classifiable today
        }

        if (!TryReserveDailyCall())
        {
            _logger.LogInformation(
                "Semantic recovery for section '{Section}' skipped: daily model-call budget ({Max}) exhausted",
                step.SectionName,
                MaxCallsPerDay);
            return null;
        }

        var intent = step.HeadingAliases.Count > 0
            ? $"{step.SectionName} (also known as: {string.Join(", ", step.HeadingAliases)})"
            : step.SectionName;

        SectionClassification classification;
        try
        {
            classification = await _analyzer.ClassifySectionAsync(candidateLabels, intent, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Semantic recovery model call failed for section '{Section}'", step.SectionName);
            return null;
        }

        if (string.IsNullOrWhiteSpace(classification.CandidateLabel) || classification.Confidence < MinConfidence)
        {
            _logger.LogInformation(
                "Semantic recovery declined for '{Section}': label='{Label}' confidence={Confidence:F2} (min {Min:F2})",
                step.SectionName,
                classification.CandidateLabel ?? "(none)",
                classification.Confidence,
                MinConfidence);
            return null;
        }

        // SELF-TEST: the durable matcher must re-match ≥1 content article under the
        // classified heading (re-run the match, confirm >0) before we trust it — a
        // confident-but-empty answer is never published.
        var matchedSection = config.Sections.FirstOrDefault(s =>
            string.Equals(s.Name, step.SectionName, StringComparison.OrdinalIgnoreCase));
        var runLocalSection = new HierarchySection
        {
            Name = classification.CandidateLabel!,
            SortOrder = matchedSection?.SortOrder ?? step.SortOrderFallback,
        };
        var matched = content.Where(l => NavigationTreeBuilder.MatchesSection(l, runLocalSection)).ToList();
        if (matched.Count == 0)
        {
            _logger.LogInformation(
                "Semantic recovery self-test failed for '{Section}': classified heading '{Label}' re-matched 0 articles",
                step.SectionName,
                classification.CandidateLabel);
            return null;
        }

        var taken = step.TakeMode switch
        {
            TakeMode.SingleTopStory => matched.Take(1),
            TakeMode.TopN => matched.Take(step.TakeCount ?? 1),
            _ => matched.Take(NavigationTreeBuilder.MaxContentLinks),
        };
        var items = taken
            .Select(l => (l.Url, Title: string.IsNullOrWhiteSpace(l.DisplayText) ? l.Url : l.DisplayText))
            .ToList();

        _logger.LogInformation(
            "Semantic recovery accepted for '{Section}' as today's heading '{Label}' (confidence {Confidence:F2}, {Count} articles)",
            step.SectionName,
            classification.CandidateLabel,
            classification.Confidence,
            matched.Count);

        return new SectionResolution
        {
            Status = ResolutionStatus.Recovered,
            Items = items,
            MatchCount = matched.Count,
            Tier = SectionMatchTier.HeadingName,
            Diagnostic = $"Semantically recovered '{step.SectionName}' as today's heading " +
                         $"'{classification.CandidateLabel}' (confidence {classification.Confidence:P0}) — " +
                         "ratify the layout in Schedules (the recovery is not saved).",
        };
    }

    /// <summary>Reserves one model call for today, rolling the counter over at local midnight.</summary>
    private bool TryReserveDailyCall()
    {
        var today = DateOnly.FromDateTime(_timeProvider.GetLocalNow().DateTime);
        lock (_budgetLock)
        {
            if (today != _budgetDay)
            {
                _budgetDay = today;
                _callsToday = 0;
            }

            if (_callsToday >= MaxCallsPerDay)
            {
                return false;
            }

            _callsToday++;
            return true;
        }
    }
}
