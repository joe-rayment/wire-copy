// Licensed under the MIT License. See LICENSE in the repository root.

using System.Xml.Linq;
using TermReader.Application.Interfaces;
using TermReader.Domain.Entities.Collections;

namespace TermReader.Infrastructure.Collections;

/// <summary>
/// Exports a collection as an OPML 2.0 XML file.
/// </summary>
public class OpmlExporter : ICollectionExporter
{
    public string Format => "opml";

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

        var name = options.CustomName ?? collection.Name;
        var datestamp = options.IncludeDatestamp
            ? DateTime.UtcNow.ToString("R")
            : null;

        var head = new XElement("head",
            new XElement("title", name));

        if (datestamp != null)
        {
            head.Add(new XElement("dateCreated", datestamp));
        }

        var body = new XElement("body");
        foreach (var item in collection.Items)
        {
            body.Add(new XElement("outline",
                new XAttribute("text", item.Title),
                new XAttribute("type", "link"),
                new XAttribute("url", item.Url),
                new XAttribute("created", item.SavedAt.ToString("R"))));
        }

        var opml = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XElement("opml",
                new XAttribute("version", "2.0"),
                head,
                body));

        var directory = Path.GetDirectoryName(options.OutputPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(options.OutputPath);
        await opml.SaveAsync(stream, SaveOptions.None, cancellationToken).ConfigureAwait(false);
    }
}
