// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace TermReader.Infrastructure.Health;

/// <summary>
/// Health check to verify sufficient disk space is available.
/// </summary>
public class DiskSpaceHealthCheck : IHealthCheck
{
    private readonly long _minimumFreeMB;
    private readonly ILogger<DiskSpaceHealthCheck> _logger;

    public DiskSpaceHealthCheck(
        ILogger<DiskSpaceHealthCheck> logger,
        long minimumFreeMB = 1000)
    {
        _logger = logger;
        _minimumFreeMB = minimumFreeMB;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var currentDir = Directory.GetCurrentDirectory();
            var drive = new DriveInfo(Path.GetPathRoot(currentDir) ?? "/");

            var freeMB = drive.AvailableFreeSpace / (1024 * 1024);
            var totalMB = drive.TotalSize / (1024 * 1024);
            var percentFree = (freeMB * 100.0) / totalMB;

            var data = new Dictionary<string, object>
            {
                ["freeSpaceMB"] = freeMB,
                ["totalSpaceMB"] = totalMB,
                ["percentFree"] = Math.Round(percentFree, 2)
            };

            if (freeMB >= _minimumFreeMB)
            {
                return Task.FromResult(HealthCheckResult.Healthy(
                    $"{freeMB:N0} MB free ({percentFree:F1}%)",
                    data));
            }

            return Task.FromResult(HealthCheckResult.Degraded(
                $"Low disk space: {freeMB:N0} MB free (minimum: {_minimumFreeMB:N0} MB)",
                data: data));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Disk space health check failed");
            return Task.FromResult(HealthCheckResult.Unhealthy(
                "Failed to check disk space", ex));
        }
    }
}
