// <copyright file="ICacheService.cs" company="TermReader">
// Educational and personal use only.
// </copyright>


namespace TermReader.Application.Interfaces;

/// <summary>
/// Generic cache service interface
/// </summary>
/// <typeparam name="T">Type of cached items</typeparam>
public interface ICacheService<T> where T : class
{
    /// <summary>
    /// Gets an item from the cache
    /// </summary>
    Task<T?> GetAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets an item in the cache with optional expiration
    /// </summary>
    Task SetAsync(string key, T value, TimeSpan? expiration = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes an item from the cache
    /// </summary>
    Task RemoveAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if an item exists in the cache
    /// </summary>
    Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears all items from the cache
    /// </summary>
    Task ClearAsync(CancellationToken cancellationToken = default);
}
