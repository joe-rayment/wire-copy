// Educational and personal use only.

using TermReader.Domain.Entities.Collections;

namespace TermReader.Application.Interfaces;

/// <summary>
/// Interface for exporting collections to various formats.
/// </summary>
public interface ICollectionExporter
{
    /// <summary>
    /// The format identifier (e.g., "urls", "opml", "html", "json").
    /// </summary>
    string Format { get; }

    /// <summary>
    /// Exports a collection to the specified output path.
    /// </summary>
    Task ExportAsync(Collection collection, ExportOptions options, CancellationToken cancellationToken = default);
}

/// <summary>
/// Options for collection export.
/// </summary>
/// <param name="OutputPath">File path for the exported output.</param>
/// <param name="IncludeDatestamp">Whether to include a datestamp in the output.</param>
/// <param name="CustomName">Optional custom name for the export.</param>
public record ExportOptions(
    string OutputPath,
    bool IncludeDatestamp = true,
    string? CustomName = null);
