// Educational and personal use only.

using Microsoft.EntityFrameworkCore;
using TermReader.Application.Interfaces;
using TermReader.Domain.Entities.Credentials;

namespace TermReader.Persistence.Repositories;

/// <summary>
/// Repository implementation for site credential persistence using EF Core.
/// </summary>
public class SiteCredentialRepository : ISiteCredentialRepository
{
    private readonly AppDbContext _context;

    public SiteCredentialRepository(AppDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public async Task<SiteCredential?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _context.Set<SiteCredential>()
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
    }

    public async Task<SiteCredential?> GetByDomainAsync(string domain, CancellationToken cancellationToken = default)
    {
        var normalizedDomain = domain.ToLower();
        return await _context.Set<SiteCredential>()
            .FirstOrDefaultAsync(c => c.Domain.ToLower() == normalizedDomain, cancellationToken);
    }

    public async Task<IReadOnlyList<SiteCredential>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await _context.Set<SiteCredential>()
            .OrderBy(c => c.Domain)
            .ToListAsync(cancellationToken);
    }

    public Task AddAsync(SiteCredential credential, CancellationToken cancellationToken = default)
    {
        _context.Set<SiteCredential>().Add(credential);
        return Task.CompletedTask;
    }

    public Task UpdateAsync(SiteCredential credential, CancellationToken cancellationToken = default)
    {
        // Rely on EF Core change tracking to detect modifications.
        // SaveChangesAsync is called by the service layer via IUnitOfWork.
        return Task.CompletedTask;
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var credential = await _context.Set<SiteCredential>()
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

        if (credential != null)
        {
            _context.Set<SiteCredential>().Remove(credential);
        }
    }
}
