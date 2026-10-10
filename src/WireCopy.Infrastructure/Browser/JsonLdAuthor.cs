// Licensed under the MIT License. See LICENSE in the repository root.

using System.Text.Json;

namespace WireCopy.Infrastructure.Browser;

/// <summary>
/// Shared, defensive reader for the "author" of a JSON-LD block. JSON-LD in the wild has no
/// fixed shape: the root may be an object, an array of nodes, or an object carrying the real
/// nodes under "@graph" (Yoast/WordPress), and "author"/"name" may be a string, object, array
/// or anything else. Every kind is checked before it is read, so an unexpected shape yields
/// "no author" instead of an <see cref="InvalidOperationException"/>.
/// </summary>
internal static class JsonLdAuthor
{
    // Root -> "@graph" -> node is the deepest legitimate nesting; the cap only stops pathological input.
    private const int MaxDepth = 4;

    /// <summary>
    /// Returns the first non-empty author found in <paramref name="root"/>: the root object itself,
    /// each item of an array root, and any "@graph" nodes, in document order. Null when none.
    /// </summary>
    public static string? Find(JsonElement root) => FindIn(root, 0);

    /// <summary>
    /// Reads an "author" value: a string, an object with a string "name", or an array of those
    /// (joined with ", "). Anything else, and blank names, give null. The result is trimmed.
    /// </summary>
    public static string? ParseAuthor(JsonElement authorElement)
    {
        switch (authorElement.ValueKind)
        {
            case JsonValueKind.String:
            case JsonValueKind.Object:
                return NameOf(authorElement);

            case JsonValueKind.Array:
                var names = new List<string>();
                foreach (var item in authorElement.EnumerateArray())
                {
                    var name = NameOf(item);
                    if (name != null)
                    {
                        names.Add(name);
                    }
                }

                return names.Count > 0 ? string.Join(", ", names) : null;

            default:
                return null;
        }
    }

    private static string? FindIn(JsonElement node, int depth)
    {
        if (depth >= MaxDepth)
        {
            return null;
        }

        switch (node.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in node.EnumerateArray())
                {
                    var fromItem = FindIn(item, depth + 1);
                    if (fromItem != null)
                    {
                        return fromItem;
                    }
                }

                return null;

            case JsonValueKind.Object:
                if (node.TryGetProperty("author", out var authorElement))
                {
                    var author = ParseAuthor(authorElement);
                    if (author != null)
                    {
                        return author;
                    }
                }

                return node.TryGetProperty("@graph", out var graph)
                    ? FindIn(graph, depth + 1)
                    : null;

            default:
                return null;
        }
    }

    private static string? NameOf(JsonElement element)
    {
        var raw = element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Object when element.TryGetProperty("name", out var nameEl) &&
                                       nameEl.ValueKind == JsonValueKind.String => nameEl.GetString(),
            _ => null
        };

        var trimmed = raw?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
