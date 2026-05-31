// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Application.DTOs.Scheduling;
using WireCopy.Application.Interfaces.Scheduling;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.Enums.Scheduling;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Domain.ValueObjects.Scheduling;
using WireCopy.Infrastructure.Browser;

namespace WireCopy.Infrastructure.Scheduling;

/// <summary>
/// workspace-frpl.3 — resolves a recipe step's durable (UrlPattern, SectionName)
/// reference to today's articles. Resolves the TARGET section IN ISOLATION using
/// the same per-criterion predicates as <see cref="NavigationTreeBuilder"/>, so
/// an earlier section whose selectors overlap can never under-count it (unlike
/// the greedy whole-tree build). Matches by selector/url-pattern/heading-name
/// first (the durable path that carries the Business Daily ↔ Sunday Business
/// case); falls back to recipe-local heading aliases only when those yield zero;
/// returns an explicit ZeroMatch/SectionNotFound (never empty-as-success).
/// </summary>
internal sealed class SectionResolver : ISectionResolver
{
    public SectionResolution Resolve(SiteHierarchyConfig config, IReadOnlyList<LinkInfo> todaysLinks, RecipeStep step)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(todaysLinks);
        ArgumentNullException.ThrowIfNull(step);

        var section = config.Sections.FirstOrDefault(s =>
                          string.Equals(s.Name, step.SectionName, StringComparison.OrdinalIgnoreCase))
                      ?? config.Sections.FirstOrDefault(s => s.SortOrder == step.SortOrderFallback);

        if (section == null)
        {
            return new SectionResolution
            {
                Status = ResolutionStatus.SectionNotFound,
                Diagnostic = $"Section '{step.SectionName}' (or SortOrder {step.SortOrderFallback}) is not in the " +
                             $"saved layout for {config.Domain} — re-run AI setup (Ctrl+l) for this site.",
            };
        }

        // Content links in document order, ads/promos removed by the durable
        // exclude rules — so SingleTopStory can't pick up a co-located ad.
        var content = todaysLinks
            .Where(l => l.Type == LinkType.Content && !l.IsGroupHeader && !NavigationTreeBuilder.IsExcluded(l, config))
            .ToList();

        // Primary: the durable predicates, resolved for THIS section only.
        var matched = content.Where(l => NavigationTreeBuilder.MatchesSection(l, section)).ToList();
        SectionMatchTier? tier = matched.Count > 0 ? ClassifyTier(matched, section) : null;

        // Fallback tier (recipe-local heading aliases) only when the durable
        // predicates matched nothing — never a false positive when they did.
        if (matched.Count == 0 && step.HeadingAliases.Count > 0)
        {
            matched = content
                .Where(l => l.SectionTitle != null &&
                            step.HeadingAliases.Any(a => string.Equals(a, l.SectionTitle, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (matched.Count > 0)
            {
                tier = SectionMatchTier.HeadingAlias;
            }
        }

        // workspace-frpl.9 (B9a) Tier-1 RECOVERY: the saved selectors are over-specific
        // (a hashed CSS class rotated, so the full saved selector no longer substring-
        // matches), but one of their stable discriminating TOKENS still does. Generalize
        // to the live tokens, re-match, and flag the result Recovered (run-LOCAL only —
        // never written back to the shared config; B11 asks the user to ratify it).
        // Deterministic, no model call.
        var recoveredSelectors = new List<string>();
        if (matched.Count == 0)
        {
            recoveredSelectors = LiveSelectorTokens(section, content);
            if (recoveredSelectors.Count > 0)
            {
                var recoveredSection = new HierarchySection
                {
                    Name = section.Name,
                    SortOrder = section.SortOrder,
                    ParentSelectors = recoveredSelectors,
                };
                matched = content.Where(l => NavigationTreeBuilder.MatchesSection(l, recoveredSection)).ToList();
            }
        }

        var recovered = matched.Count > 0 && recoveredSelectors.Count > 0;

        if (matched.Count == 0)
        {
            return new SectionResolution
            {
                Status = ResolutionStatus.ZeroMatch,
                MatchCount = 0,
                Diagnostic = $"Section '{section.Name}' matched 0 articles on {config.Domain} today; " +
                             "the site may have changed — re-run AI setup (Ctrl+l).",
            };
        }

        var taken = step.TakeMode switch
        {
            TakeMode.SingleTopStory => matched.Take(1),
            TakeMode.TopN => matched.Take(step.TakeCount ?? 1),
            _ => matched.Take(NavigationTreeBuilder.MaxContentLinks),
        };

        var items = taken.Select(l => (l.Url, Title: string.IsNullOrWhiteSpace(l.DisplayText) ? l.Url : l.DisplayText)).ToList();
        return new SectionResolution
        {
            Status = recovered ? ResolutionStatus.Recovered : ResolutionStatus.Resolved,
            Items = items,
            MatchCount = matched.Count,
            Tier = recovered ? SectionMatchTier.Selector : tier,
            Diagnostic = recovered
                ? $"Recovered '{section.Name}' on {config.Domain} via re-derived selector(s) " +
                  $"[{string.Join(", ", recoveredSelectors)}] after the saved selector matched 0 today — " +
                  "ratify the layout in Schedules (the recovery is not saved)."
                : null,
        };
    }

    /// <summary>
    /// The section's saved-selector discriminating tokens (e.g. <c>section.business</c>)
    /// that STILL substring-match ≥1 of today's content links. A non-empty result means
    /// the saved selector was merely over-specific (a sibling token rotated) and can be
    /// safely loosened for THIS run; an empty result means there is no live anchor and the
    /// section is genuinely unresolvable today. A single-token saved selector that already
    /// failed yields nothing here (the token equals the full selector), so a clean
    /// ZeroMatch never spuriously "recovers".
    /// </summary>
    private static List<string> LiveSelectorTokens(HierarchySection section, IReadOnlyList<LinkInfo> content) =>
        section.ParentSelectors
            .SelectMany(SelectorDerivation.DiscriminatingTokens)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(token => content.Any(l =>
                l.ParentSelector != null &&
                l.ParentSelector.Contains(token, StringComparison.OrdinalIgnoreCase)))
            .ToList();

    private static SectionMatchTier ClassifyTier(IEnumerable<LinkInfo> matched, HierarchySection section)
    {
        var list = matched as IReadOnlyList<LinkInfo> ?? matched.ToList();
        if (list.Any(l => NavigationTreeBuilder.MatchesByParentSelector(l, section)))
        {
            return SectionMatchTier.Selector;
        }

        if (list.Any(l => NavigationTreeBuilder.MatchesByUrlPattern(l, section)))
        {
            return SectionMatchTier.UrlPattern;
        }

        return SectionMatchTier.HeadingName;
    }
}
