// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Domain.Entities.Scheduling;
using WireCopy.Domain.Enums.Scheduling;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Domain.ValueObjects.Scheduling;

namespace WireCopy.Infrastructure.Scheduling;

/// <summary>
/// workspace-wyvga.9 (S5): plain state for one Routines pick session (DI singleton).
/// Picks survive <see cref="Active"/> = false until the next <see cref="Start"/>.
/// </summary>
public sealed class RoutinePickSession
{
    public bool Active { get; set; }

    public Guid RoutineId { get; set; }

    public string RoutineName { get; set; } = string.Empty;

    public string SiteUrl { get; set; } = string.Empty;

    public SiteHierarchyConfig? Config { get; set; }

    public bool ConfigIsNew { get; set; }

    public IReadOnlyList<LinkInfo> TodaysLinks { get; set; } = Array.Empty<LinkInfo>();

    public Dictionary<string, SectionPick> Picks { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Resets all state and begins a new session.</summary>
    public void Start(
        Guid routineId,
        string routineName,
        string siteUrl,
        SiteHierarchyConfig config,
        bool configIsNew,
        IReadOnlyList<LinkInfo> todaysLinks)
    {
        RoutineId = routineId;
        RoutineName = routineName;
        SiteUrl = siteUrl;
        Config = config;
        ConfigIsNew = configIsNew;
        TodaysLinks = todaysLinks;
        Picks = new Dictionary<string, SectionPick>(StringComparer.OrdinalIgnoreCase);
        Active = true;
    }

    /// <summary>Loads picks from the recipe's steps for the same site (same Domain + ConfigUrlPattern).</summary>
    public void FromSteps(ScheduleRecipe recipe, string siteDomain, string configUrlPattern)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        Picks.Clear();
        foreach (var step in recipe.Steps.Where(s => IsSameSite(s, siteDomain, configUrlPattern)))
        {
            var pick = new SectionPick { Scope = step.Scope, TakeMode = step.TakeMode, TakeCount = step.TakeCount };
            foreach (var p in step.Positions)
            {
                pick.Positions.Add(p);
            }

            Picks[step.SectionName] = pick;
        }
    }

    /// <summary>Builds RecipeSteps for the current picks (S4 "Steps created by the Routines UI").</summary>
    public List<RecipeStep> ToSteps()
    {
        var config = Config ?? throw new InvalidOperationException("No site config in the pick session");
        var domain = WireCopy.Infrastructure.Browser.HierarchyDomainKey.FromUrl(SiteUrl);
        var steps = new List<RecipeStep>();
        foreach (var (name, pick) in Picks)
        {
            var wholePage = pick.Scope == StepScope.WholePage;
            var sectionName = wholePage ? RecipeStep.WholePageSectionName : name;
            var sortOrder = config.Sections
                .FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))?.SortOrder ?? 0;
            var step = RecipeStep.Create(
                SiteUrl,
                domain,
                config.UrlPattern,
                sectionName,
                pick.TakeMode,
                pick.TakeMode == TakeMode.TopN ? pick.TakeCount : null,
                required: false,
                sortOrderFallback: sortOrder,
                scope: pick.Scope);
            if (pick.TakeMode == TakeMode.Slots)
            {
                step = step with { Positions = pick.Positions.Where(p => p >= 1).Distinct().OrderBy(p => p).ToList() };
            }

            steps.Add(step);
        }

        return steps;
    }

    /// <summary>
    /// Returns the recipe's new step list: this session's steps replace the recipe's steps for the SAME
    /// site (Domain + ConfigUrlPattern), at the position of the first replaced step (or appended when
    /// the site is new); every other site's steps stay untouched in their original order.
    /// The recipe itself is not modified.
    /// </summary>
    public List<RecipeStep> MergeInto(ScheduleRecipe recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        var config = Config ?? throw new InvalidOperationException("No site config in the pick session");
        var domain = WireCopy.Infrastructure.Browser.HierarchyDomainKey.FromUrl(SiteUrl);
        var fresh = ToSteps();
        var result = new List<RecipeStep>();
        var inserted = false;
        foreach (var step in recipe.Steps)
        {
            if (IsSameSite(step, domain, config.UrlPattern))
            {
                if (!inserted)
                {
                    result.AddRange(fresh);
                    inserted = true;
                }

                continue;
            }

            result.Add(step);
        }

        if (!inserted)
        {
            result.AddRange(fresh);
        }

        return result;
    }

    private static bool IsSameSite(RecipeStep s, string domain, string configUrlPattern) =>
        string.Equals(s.Domain, domain, StringComparison.OrdinalIgnoreCase)
        && string.Equals(s.ConfigUrlPattern, configUrlPattern, StringComparison.Ordinal);
}
