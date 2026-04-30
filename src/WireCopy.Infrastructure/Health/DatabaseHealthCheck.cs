// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using WireCopy.Persistence;

namespace WireCopy.Infrastructure.Health;

/// <summary>
/// Health check to verify database connectivity and migrations.
/// </summary>
public class DatabaseHealthCheck : IHealthCheck
{
    private readonly AppDbContext _dbContext;
    private readonly ILogger<DatabaseHealthCheck> _logger;

    public DatabaseHealthCheck(
        AppDbContext dbContext,
        ILogger<DatabaseHealthCheck> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Check if database can be accessed
            var canConnect = await _dbContext.Database.CanConnectAsync(cancellationToken).ConfigureAwait(false);

            if (!canConnect)
            {
                return HealthCheckResult.Unhealthy("Cannot connect to database");
            }

            // Check if migrations are applied
            var pendingMigrations = await _dbContext.Database
                .GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false);

            var pendingMigrationsList = pendingMigrations.ToList();

            if (pendingMigrationsList.Any())
            {
                return HealthCheckResult.Degraded(
                    $"{pendingMigrationsList.Count} pending migration(s) - will be applied automatically",
                    data: new Dictionary<string, object>
                    {
                        ["pendingMigrations"] = pendingMigrationsList.Count
                    });
            }

            return HealthCheckResult.Healthy(
                "Database is accessible and up-to-date");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Database health check failed");
            return HealthCheckResult.Unhealthy(
                "Database health check failed", ex);
        }
    }
}
