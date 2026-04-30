// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Application.Interfaces;
using WireCopy.Domain.Entities.Collections;

namespace WireCopy.Infrastructure.Collections;

/// <summary>
/// Exports a collection as a plain text file with one URL per line.
/// </summary>
public class UrlListExporter : ICollectionExporter
{
    public string Format => "urls";

    public async Task ExportAsync(Collection collection, ExportOptions options, CancellationToken cancellationToken = default)
    {
        if (collection == null)
        {
            throw new ArgumentNullException(nameof(collection));
        }

        if (options == null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        var lines = new List<string>();

        if (options.IncludeDatestamp)
        {
            var name = options.CustomName ?? collection.Name;
            lines.Add($"# {name} - exported {DateTime.UtcNow:yyyy-MM-dd}");
            lines.Add(string.Empty);
        }

        foreach (var item in collection.Items)
        {
            lines.Add(item.Url);
        }

        var directory = Path.GetDirectoryName(options.OutputPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllLinesAsync(options.OutputPath, lines, cancellationToken).ConfigureAwait(false);
    }
}
