// Licensed under the MIT License. See LICENSE in the repository root.

namespace TermReader.Application.Interfaces.Browser;

/// <summary>
/// Registry of scraping strategies. Order matches the order they appear
/// in the chooser (DocumentOrder first, AiCurated, then RssFeed).
/// </summary>
public interface IScrapingStrategyRegistry
{
    /// <summary>All known strategies, in chooser order.</summary>
    IReadOnlyList<IScrapingStrategy> All { get; }

    /// <summary>Look up a strategy by its <see cref="IScrapingStrategy.Id"/>.</summary>
    IScrapingStrategy? GetById(string id);
}
