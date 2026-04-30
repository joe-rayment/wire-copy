// Licensed under the MIT License. See LICENSE in the repository root.

using TermReader.Application.Interfaces.Browser;

namespace TermReader.Infrastructure.Browser.ScrapingStrategies;

/// <summary>
/// Default registry. DI registers concrete strategies; this list orders
/// them for the chooser (DocumentOrder, AiCurated, RssFeed).
/// </summary>
public sealed class ScrapingStrategyRegistry : IScrapingStrategyRegistry
{
    private static readonly string[] DisplayOrder =
    {
        DocumentOrderStrategy.StrategyId,
        AiCuratedStrategy.StrategyId,
        RssFeedStrategy.StrategyId,
    };

    private readonly List<IScrapingStrategy> _ordered;

    public ScrapingStrategyRegistry(IEnumerable<IScrapingStrategy> strategies)
    {
        var byId = strategies.ToDictionary(s => s.Id, s => s, StringComparer.Ordinal);
        _ordered = new List<IScrapingStrategy>();
        foreach (var id in DisplayOrder)
        {
            if (byId.TryGetValue(id, out var strategy))
            {
                _ordered.Add(strategy);
                byId.Remove(id);
            }
        }

        // Append any unrecognised strategies at the end (forward-compat).
        _ordered.AddRange(byId.Values);
    }

    public IReadOnlyList<IScrapingStrategy> All => _ordered;

    public IScrapingStrategy? GetById(string id)
        => _ordered.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal));
}
