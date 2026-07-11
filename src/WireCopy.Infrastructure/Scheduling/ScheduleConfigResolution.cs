// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Domain.ValueObjects.Scheduling;
using WireCopy.Infrastructure.Browser;

namespace WireCopy.Infrastructure.Scheduling;

/// <summary>
/// workspace-42q8.1 — the ONE way schedule code finds a step's saved layout config.
/// A <see cref="RecipeStep"/>'s durable identity is (ConfigUrlPattern, SectionName),
/// but every pre-existing call site looked the config up by regex-matching URLs
/// instead, so URL drift (www vs apex host, bookmark URL vs the URL the layout was
/// saved on, post-redirect finals) produced false "has no saved layout" verdicts at
/// add-time, edit-time AND run-time. Resolution order here: exact
/// <c>ConfigUrlPattern</c> equality among the site's configs FIRST, then the store's
/// URL match on the source URL, then on the post-redirect final URL. The result also
/// says whether the site has ANY saved config, so callers can stop conflating
/// "nothing saved for this site" with "this page isn't covered".
/// </summary>
internal static class ScheduleConfigResolution
{
    /// <summary>Resolves the config for an existing recipe step (edit + run time).</summary>
    public static async Task<ScheduleConfigLookup> ForStepAsync(
        IHierarchyConfigStore store,
        RecipeStep step,
        string? finalUrl = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(step);

        var siteConfigs = await store.GetConfigsForDomainAsync(step.SourceUrl).ConfigureAwait(false);

        // The durable key, exactly as RecipeStep declares it.
        var byPattern = siteConfigs.FirstOrDefault(c =>
            string.Equals(c.UrlPattern, step.ConfigUrlPattern, StringComparison.Ordinal));
        if (byPattern != null)
        {
            return new ScheduleConfigLookup { Config = byPattern, SiteConfigs = siteConfigs };
        }

        var byUrl = await store.GetConfigAsync(step.SourceUrl).ConfigureAwait(false);
        if (byUrl == null && !string.IsNullOrEmpty(finalUrl) &&
            !string.Equals(finalUrl, step.SourceUrl, StringComparison.OrdinalIgnoreCase))
        {
            byUrl = await store.GetConfigAsync(finalUrl).ConfigureAwait(false);
            if (siteConfigs.Count == 0)
            {
                // A redirect can land on a different registrable site; make
                // SiteHasAnyConfig reflect where the content actually lives.
                siteConfigs = await store.GetConfigsForDomainAsync(finalUrl).ConfigureAwait(false);
            }
        }

        return new ScheduleConfigLookup { Config = byUrl, SiteConfigs = siteConfigs };
    }

    /// <summary>Resolves the config situation for a URL being ADDED as a step (no step exists yet).</summary>
    public static async Task<ScheduleConfigLookup> ForUrlAsync(IHierarchyConfigStore store, string url)
    {
        ArgumentNullException.ThrowIfNull(store);

        var siteConfigs = await store.GetConfigsForDomainAsync(url).ConfigureAwait(false);
        var config = await store.GetConfigAsync(url).ConfigureAwait(false);
        return new ScheduleConfigLookup { Config = config, SiteConfigs = siteConfigs };
    }

    /// <summary>
    /// A short human description of where a site's saved layout(s) apply, for honest
    /// "this page isn't covered, but the site IS set up" messages — e.g.
    /// "/section/todayspaper" or "2 saved layouts".
    /// </summary>
    public static string DescribeSitePatterns(IReadOnlyList<SiteHierarchyConfig> siteConfigs)
    {
        ArgumentNullException.ThrowIfNull(siteConfigs);
        if (siteConfigs.Count == 0)
        {
            return "none";
        }

        if (siteConfigs.Count > 1)
        {
            return $"{siteConfigs.Count} saved layouts";
        }

        return HumanizePattern(siteConfigs[0].UrlPattern);
    }

    /// <summary>Best-effort de-regexed path for display ("^https?://(www\.)?x\.com/a\/b" → "/a/b").</summary>
    internal static string HumanizePattern(string urlPattern)
    {
        if (string.IsNullOrWhiteSpace(urlPattern))
        {
            return "the site";
        }

        var cleaned = urlPattern
            .Replace("^https?://", string.Empty, StringComparison.Ordinal)
            .Replace("(www\\.)?", string.Empty, StringComparison.Ordinal)
            .Replace("\\", string.Empty, StringComparison.Ordinal)
            .TrimEnd('$');
        var slash = cleaned.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0)
        {
            return cleaned;
        }

        var path = cleaned[slash..].TrimEnd('?');
        return path.Length <= 1 ? cleaned[..slash] + " (home page)" : path;
    }
}
