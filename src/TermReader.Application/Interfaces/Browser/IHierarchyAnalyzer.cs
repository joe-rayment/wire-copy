// Licensed under the MIT License. See LICENSE in the repository root.

using TermReader.Domain.ValueObjects.Browser;

namespace TermReader.Application.Interfaces.Browser;

public interface IHierarchyAnalyzer
{
    bool IsConfigured { get; }

    Task<SiteHierarchyConfig> AnalyzePageHierarchyAsync(
        byte[] screenshot,
        List<LinkInfo> links,
        string pageUrl,
        string? promptSuffix = null,
        CancellationToken cancellationToken = default);

    Task<AiCuratedResult> AnalyzeCuratedAsync(
        byte[]? screenshot,
        List<LinkInfo> links,
        string pageUrl,
        CancellationToken cancellationToken = default);
}
