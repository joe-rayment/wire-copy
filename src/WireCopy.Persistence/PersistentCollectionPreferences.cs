// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Application.Interfaces;

namespace WireCopy.Persistence;

/// <summary>
/// Persists collection preferences (e.g., last-used collection) via <see cref="IUserSettingsStore"/>
/// so they survive app restarts.
/// </summary>
public sealed class PersistentCollectionPreferences : ICollectionPreferences
{
    private const string LastUsedCollectionKey = "LastUsedCollectionId";

    private readonly IUserSettingsStore _settingsStore;

    /// <summary>
    /// In-memory cache so repeated reads within the same session avoid disk I/O.
    /// Null means "not yet loaded from store" (lazy); <see cref="Guid.Empty"/>-sentinel is never stored.
    /// </summary>
    private Guid? _cached;
    private bool _loaded;

    public PersistentCollectionPreferences(IUserSettingsStore settingsStore)
    {
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
    }

    public Guid? LastUsedCollectionId
    {
        get
        {
            if (!_loaded)
            {
                var raw = _settingsStore.Get(LastUsedCollectionKey);
                _cached = Guid.TryParse(raw, out var parsed) ? parsed : null;
                _loaded = true;
            }

            return _cached;
        }

        set
        {
            _cached = value;
            _loaded = true;

            if (value.HasValue)
            {
                _settingsStore.Set(LastUsedCollectionKey, value.Value.ToString());
            }
            else
            {
                _settingsStore.Remove(LastUsedCollectionKey);
            }
        }
    }
}
