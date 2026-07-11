// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Domain.Enums.Scheduling;

namespace WireCopy.Domain.ValueObjects.Scheduling;

/// <summary>
/// workspace-frpl.2 — one ordered source in a recipe: "from this site, take this
/// section, this many articles". The section is referenced by its DURABLE
/// identity — the saved config's <c>UrlPattern</c> + section <c>Name</c> (plus a
/// <see cref="SortOrderFallback"/> for a renamed section) — and is resolved LIVE
/// against the current <c>SiteHierarchyConfig</c> at run time (B2). It stores NO
/// selector snapshot, NO heading text as a key, and NO article URLs, so it
/// survives heading changes (Business Daily ↔ Sunday Business) and URL rotation.
/// <see cref="HeadingAliases"/> is a recipe-local fallback tier only.
/// </summary>
public sealed record RecipeStep
{
    /// <summary>Optional source bookmark this step was built from.</summary>
    public Guid? BookmarkId { get; init; }

    /// <summary>The homepage URL to load for this step.</summary>
    public required string SourceUrl { get; init; }

    /// <summary>Domain (lowercased host) — for config lookup + diagnostics.</summary>
    public required string Domain { get; init; }

    /// <summary>The saved config's UrlPattern this step's section lives under (durable key part 1).</summary>
    public required string ConfigUrlPattern { get; init; }

    /// <summary>The saved section's Name (durable key part 2).</summary>
    public required string SectionName { get; init; }

    /// <summary>
    /// workspace-42q8.2 — whether this step pins a named section or takes the whole
    /// page. Defaults to <see cref="StepScope.PinnedSection"/> so recipes persisted
    /// before the field existed keep their meaning.
    /// </summary>
    public StepScope Scope { get; init; } = StepScope.PinnedSection;

    /// <summary>SortOrder to fall back to if the section was renamed.</summary>
    public int SortOrderFallback { get; init; }

    /// <summary>Recipe-local heading aliases — a LAST-tier match fallback, never the primary key.</summary>
    public List<string> HeadingAliases { get; init; } = new();

    public TakeMode TakeMode { get; init; } = TakeMode.WholeSection;

    /// <summary>Article count: null for WholeSection, 1 for SingleTopStory, ≥1 for TopN.</summary>
    public int? TakeCount { get; init; }

    /// <summary>When true, the run's quality floor (B7) fails if this step contributes nothing.</summary>
    public bool Required { get; init; }

    /// <summary>
    /// The display name a whole-page step carries in place of a section name.
    /// </summary>
    public const string WholePageSectionName = "All stories";

    /// <summary>
    /// Validates + normalises the TakeMode/TakeCount invariants:
    /// SingleTopStory ⇒ count 1, WholeSection ⇒ count null, TopN ⇒ count ≥ 1.
    /// A <see cref="StepScope.WholePage"/> step needs no section name (it defaults
    /// to <see cref="WholePageSectionName"/>); a pinned-section step requires one.
    /// </summary>
    public static RecipeStep Create(
        string sourceUrl,
        string domain,
        string configUrlPattern,
        string sectionName,
        TakeMode takeMode = TakeMode.WholeSection,
        int? takeCount = null,
        bool required = true,
        int sortOrderFallback = 0,
        Guid? bookmarkId = null,
        IEnumerable<string>? headingAliases = null,
        StepScope scope = StepScope.PinnedSection)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl))
        {
            throw new ArgumentException("SourceUrl is required", nameof(sourceUrl));
        }

        if (scope == StepScope.WholePage && string.IsNullOrWhiteSpace(sectionName))
        {
            sectionName = WholePageSectionName;
        }

        if (string.IsNullOrWhiteSpace(sectionName))
        {
            throw new ArgumentException("SectionName is required", nameof(sectionName));
        }

        var normalizedCount = takeMode switch
        {
            TakeMode.WholeSection => (int?)null,
            TakeMode.SingleTopStory => 1,
            TakeMode.TopN => takeCount is >= 1
                ? takeCount
                : throw new ArgumentException("TopN requires a count >= 1", nameof(takeCount)),
            _ => throw new ArgumentOutOfRangeException(nameof(takeMode)),
        };

        return new RecipeStep
        {
            SourceUrl = sourceUrl.Trim(),
            Domain = domain.Trim().ToLowerInvariant(),
            ConfigUrlPattern = configUrlPattern,
            SectionName = sectionName.Trim(),
            Scope = scope,
            SortOrderFallback = sortOrderFallback,
            HeadingAliases = headingAliases?.ToList() ?? new List<string>(),
            TakeMode = takeMode,
            TakeCount = normalizedCount,
            Required = required,
            BookmarkId = bookmarkId,
        };
    }
}
