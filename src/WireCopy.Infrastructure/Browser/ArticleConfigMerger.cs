// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Application.DTOs.Browser;

namespace WireCopy.Infrastructure.Browser;

/// <summary>
/// Merge helpers for <see cref="ArticleSelectorConfig"/> when the AI extractor
/// produces a fresh per-page-type entry that needs to be reconciled with the
/// existing saved layout. The MVP rule (workspace-xusy) replaced an entry by
/// Name and appended otherwise; that loses information when the AI returns
/// the same Name for genuinely different page types (e.g. NYT live-blog vs
/// article — both called "article"). workspace-v6w3 introduces a matcher-
/// shape check: name collisions only replace when the matcher describes the
/// same page shape; otherwise the new entry is appended with a deduplicated
/// Name so the "unique name" invariant on the parent config holds.
/// </summary>
internal static class ArticleConfigMerger
{
    /// <summary>
    /// Merges <paramref name="fresh"/> (always a single-entry config produced
    /// by the AI extractor) into <paramref name="existing"/>. Returns the
    /// merged config; when <paramref name="existing"/> is null or empty the
    /// fresh config is returned verbatim.
    /// </summary>
    public static ArticleSelectorConfig Merge(
        ArticleSelectorConfig? existing,
        ArticleSelectorConfig fresh)
    {
        ArgumentNullException.ThrowIfNull(fresh);

        if (existing == null || existing.PageTypes.Count == 0)
        {
            return fresh;
        }

        if (fresh.PageTypes.Count == 0)
        {
            return existing;
        }

        var freshEntry = fresh.PageTypes[0];
        var entries = new List<PageTypeEntry>(existing.PageTypes);
        var nameMatchIdx = entries.FindIndex(e =>
            string.Equals(e.Name, freshEntry.Name, StringComparison.OrdinalIgnoreCase));

        if (nameMatchIdx >= 0 && MatcherShapeMatches(entries[nameMatchIdx].Matcher, freshEntry.Matcher))
        {
            entries[nameMatchIdx] = freshEntry;
        }
        else
        {
            var unique = MakeUniqueName(freshEntry.Name, entries);
            var toAdd = unique == freshEntry.Name
                ? freshEntry
                : freshEntry with { Name = unique };
            entries.Add(toAdd);
        }

        return existing with
        {
            UpdatedAt = DateTime.UtcNow,
            PageTypes = entries,
        };
    }

    /// <summary>
    /// True when two matchers describe the same page shape: same
    /// <see cref="PageTypeMatcher.LdJsonType"/>, <see cref="PageTypeMatcher.UrlPattern"/>,
    /// and <see cref="PageTypeMatcher.BodyClassContains"/>. Used to
    /// distinguish "AI re-ran the same page-type" (replace) from "AI returned
    /// a different page-type that happens to share a name" (append).
    /// </summary>
    public static bool MatcherShapeMatches(PageTypeMatcher a, PageTypeMatcher b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        static bool EqualNullable(string? x, string? y) =>
            string.Equals(x ?? string.Empty, y ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        return EqualNullable(a.LdJsonType, b.LdJsonType)
            && EqualNullable(a.UrlPattern, b.UrlPattern)
            && EqualNullable(a.BodyClassContains, b.BodyClassContains);
    }

    /// <summary>
    /// Returns the candidate name unchanged when it does not collide with any
    /// existing entry, or appends "-2", "-3", … to make it unique.
    /// </summary>
    public static string MakeUniqueName(string candidate, IReadOnlyList<PageTypeEntry> existing)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(existing);

        bool Taken(string name) =>
            existing.Any(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

        if (!Taken(candidate))
        {
            return candidate;
        }

        for (var i = 2; i <= 99; i++)
        {
            var attempt = $"{candidate}-{i}";
            if (!Taken(attempt))
            {
                return attempt;
            }
        }

        return $"{candidate}-{Guid.NewGuid().ToString("N")[..6]}";
    }
}
