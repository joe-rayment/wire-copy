// Educational and personal use only.

using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace TermReader.Infrastructure.Browser;

/// <summary>
/// Cleans up orphaned chromedriver processes from previous crashed sessions.
/// Only targets chromedriver processes (not user Chrome) since users don't run chromedriver manually.
/// </summary>
public static class OrphanProcessCleaner
{
    /// <summary>
    /// Kills any orphaned chromedriver processes that may have been left by previous sessions.
    /// </summary>
    public static void CleanupOrphanedDrivers(ILogger? logger = null)
    {
        try
        {
            var processes = Process.GetProcessesByName("chromedriver");
            if (processes.Length == 0)
            {
                return;
            }

            logger?.LogInformation("Found {Count} orphaned chromedriver process(es), cleaning up", processes.Length);

            foreach (var process in processes)
            {
                try
                {
                    process.Kill();
                    process.WaitForExit(3000);
                    logger?.LogDebug("Killed orphaned chromedriver process {Pid}", process.Id);
                }
                catch (Exception ex)
                {
                    logger?.LogDebug(ex, "Failed to kill chromedriver process {Pid}", process.Id);
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "Error during orphaned process cleanup");
        }
    }
}
