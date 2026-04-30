// <copyright file="LegacyAppDataMigrator.cs" company="Wire Copy">
// Licensed under the MIT License. See LICENSE in the repository root.
// </copyright>

using Microsoft.Extensions.Logging;

namespace WireCopy.Infrastructure.Configuration;

/// <summary>
/// One-time migration of the local app-data directory from the legacy
/// <c>TermReader</c> name to the current <c>WireCopy</c> name.
/// </summary>
/// <remarks>
/// <para>
/// Existing installations stored their SQLite database, encrypted user
/// settings, cookie store, themes, and content cache under
/// <c>{LocalAppData}/TermReader/</c>. After the rename, the app reads/writes
/// from <c>{LocalAppData}/WireCopy/</c>. To avoid forcing every existing
/// user to lose their data, this migrator runs once at startup: if the old
/// directory exists and the new one does not, the entire directory is
/// renamed in place (a single inode rename — fast, atomic, and preserves
/// all contents). If both exist (e.g. a partial migration or a fresh-start
/// install side-by-side with old data), we abort and leave the user to
/// resolve manually so we never destroy data.
/// </para>
/// <para>
/// Safe to remove after one release once all installs have migrated.
/// </para>
/// </remarks>
public static class LegacyAppDataMigrator
{
    private const string LegacyDirName = "TermReader";
    private const string CurrentDirName = "WireCopy";

    /// <summary>
    /// Performs the migration if needed. Idempotent and best-effort: any
    /// failure is logged and swallowed so the app continues to start.
    /// </summary>
    /// <param name="logger">Logger for migration events.</param>
    public static void MigrateIfNeeded(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(localAppData))
        {
            return;
        }

        MigrateIfNeeded(logger, localAppData);
    }

    /// <summary>
    /// Performs the migration against an explicit base directory. Exposed
    /// for unit testing so the migration can run against a temporary path
    /// instead of the user's real <c>LocalApplicationData</c>.
    /// </summary>
    /// <param name="logger">Logger for migration events.</param>
    /// <param name="localAppDataRoot">The base directory that contains
    /// (or would contain) the per-app folder.</param>
    public static void MigrateIfNeeded(ILogger logger, string localAppDataRoot)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentException.ThrowIfNullOrEmpty(localAppDataRoot);

        var legacyDir = Path.Combine(localAppDataRoot, LegacyDirName);
        var currentDir = Path.Combine(localAppDataRoot, CurrentDirName);

        if (!Directory.Exists(legacyDir))
        {
            // Nothing to migrate.
            return;
        }

        if (Directory.Exists(currentDir))
        {
            // Both directories exist — refuse to overwrite. The user has
            // either already migrated (in which case the legacy dir is
            // safe to delete manually) or has data in both that needs
            // human review. Don't destroy anything.
            logger.LogWarning(
                "Both legacy ({Legacy}) and current ({Current}) app-data directories exist; skipping migration. Please resolve manually.",
                legacyDir,
                currentDir);
            return;
        }

        try
        {
            Directory.Move(legacyDir, currentDir);
            logger.LogInformation(
                "Migrated app-data directory: {Legacy} -> {Current}",
                legacyDir,
                currentDir);
        }
        catch (Exception ex)
        {
            // Best-effort: log and continue. The app will start with a
            // fresh app-data directory and the user can copy contents
            // manually if needed.
            logger.LogWarning(
                ex,
                "Failed to migrate app-data directory from {Legacy} to {Current}; continuing with new directory.",
                legacyDir,
                currentDir);
        }
    }
}
