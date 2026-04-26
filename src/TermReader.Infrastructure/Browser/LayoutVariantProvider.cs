// Licensed under the MIT License. See LICENSE in the repository root.

using TermReader.Application.Interfaces;
using TermReader.Application.Interfaces.Browser;
using TermReader.Domain.Enums.Browser;

namespace TermReader.Infrastructure.Browser;

/// <summary>
/// Manages layout variant selection per ViewMode, persisting preferences via IUserSettingsStore.
/// Thread-safe via locking on mutation.
/// </summary>
internal sealed class LayoutVariantProvider : ILayoutVariantProvider
{
    private static readonly Dictionary<ViewMode, string[]> VariantsByMode = new()
    {
        [ViewMode.Launcher] = ["Grid", "List", "Compact"],
        [ViewMode.Hierarchical] = ["Cards", "DenseList", "Magazine"],
        [ViewMode.Readable] = ["Comfortable", "FullWidth", "Narrow"],
        [ViewMode.CollectionItems] = ["Standard", "Compact"],
        [ViewMode.CollectionList] = ["Standard"],
    };

    private readonly IUserSettingsStore _settingsStore;
    private readonly object _lock = new();
    private readonly Dictionary<ViewMode, int> _currentIndices = new();

    public LayoutVariantProvider(IUserSettingsStore settingsStore)
    {
        _settingsStore = settingsStore;
        LoadPersistedPreferences();
    }

    /// <inheritdoc />
    public string GetCurrentVariant(ViewMode mode)
    {
        var variants = GetVariantsForMode(mode);
        var index = GetSafeIndex(mode, variants);
        return variants[index];
    }

    /// <inheritdoc />
    public string CycleVariant(ViewMode mode)
    {
        var variants = GetVariantsForMode(mode);
        if (variants.Length <= 1)
        {
            return variants[0];
        }

        lock (_lock)
        {
            var current = GetSafeIndex(mode, variants);
            var next = (current + 1) % variants.Length;
            _currentIndices[mode] = next;
            PersistPreference(mode, variants[next]);
            return variants[next];
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetAvailableVariants(ViewMode mode)
    {
        return GetVariantsForMode(mode);
    }

    /// <inheritdoc />
    public int GetCurrentIndex(ViewMode mode)
    {
        var variants = GetVariantsForMode(mode);
        return GetSafeIndex(mode, variants);
    }

    /// <inheritdoc />
    public int GetTotalVariants(ViewMode mode)
    {
        return GetVariantsForMode(mode).Length;
    }

    private static string GetSettingsKey(ViewMode mode) => $"Layout:{mode}";

    private static string[] GetVariantsForMode(ViewMode mode)
    {
        return VariantsByMode.TryGetValue(mode, out var variants)
            ? variants
            : ["Standard"];
    }

    private int GetSafeIndex(ViewMode mode, string[] variants)
    {
        if (_currentIndices.TryGetValue(mode, out var index) && index >= 0 && index < variants.Length)
        {
            return index;
        }

        return 0;
    }

    private void LoadPersistedPreferences()
    {
        foreach (var (mode, variants) in VariantsByMode)
        {
            var saved = _settingsStore.Get(GetSettingsKey(mode));
            if (saved != null)
            {
                var index = Array.IndexOf(variants, saved);
                if (index >= 0)
                {
                    _currentIndices[mode] = index;
                }
            }
        }
    }

    private void PersistPreference(ViewMode mode, string variantName)
    {
        try
        {
            _settingsStore.Set(GetSettingsKey(mode), variantName);
        }
        catch
        {
            // Best-effort persistence; don't crash on I/O failure
        }
    }
}
