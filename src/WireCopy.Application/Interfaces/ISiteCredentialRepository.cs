// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Domain.Entities.Credentials;

namespace WireCopy.Application.Interfaces;

/// <summary>
/// Repository interface for site credential persistence operations.
/// </summary>
public interface ISiteCredentialRepository
{
    /// <summary>
    /// Gets a site credential by its ID.
    /// </summary>
    Task<SiteCredential?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the credential for a given domain (case-insensitive match).
    /// </summary>
    Task<SiteCredential?> GetByDomainAsync(string domain, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all stored site credentials.
    /// </summary>
    Task<IReadOnlyList<SiteCredential>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a new site credential.
    /// </summary>
    Task AddAsync(SiteCredential credential, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates an existing site credential (relies on change tracking).
    /// </summary>
    Task UpdateAsync(SiteCredential credential, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a site credential by its ID.
    /// </summary>
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
