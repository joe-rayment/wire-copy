// Licensed under the MIT License. See LICENSE in the repository root.

using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace WireCopy.Infrastructure.Browser;

/// <summary>
/// Result of a <see cref="PlaywrightDriverBootstrapper.Ensure(string, ILogger)"/> check.
/// </summary>
internal enum DriverStatus
{
    /// <summary>The driver was already present (executable bit re-asserted if needed).</summary>
    Present,

    /// <summary>The driver was missing and was restored from the NuGet cache.</summary>
    Healed,

    /// <summary>The driver is missing and could not be restored (e.g. a packaged bundle with no cache).</summary>
    Missing,
}

/// <summary>
/// Startup self-heal for the Patchright/Playwright Node driver (workspace-ltre follow-up).
///
/// Patchright resolves its Node driver at <c>{AppContext.BaseDirectory}/.playwright/node/{rid}/node</c>.
/// The build copies ONLY the host RID's node into the output, so a partial/incremental rebuild after a
/// <c>clean</c>, a copy that dropped the file or its executable bit, or a broken bundle leaves it absent —
/// and then EVERY browser launch dies with "Driver not found" and the web pane goes blank, with no
/// actionable message (the exact macOS failure in workspace-ltre).
///
/// On startup we make sure the driver is present and runnable: if the node binary is there we re-assert
/// its executable bit; if it is missing we restore it from the Patchright NuGet cache (present in dev
/// runs, which bundles every RID's driver). A published self-contained bundle has no cache to restore
/// from, so if the driver is missing there we log a single clear, actionable error instead of letting
/// the user hit a cryptic stack trace on every page.
/// </summary>
internal static class PlaywrightDriverBootstrapper
{
    public static DriverStatus Ensure(ILogger logger) => Ensure(AppContext.BaseDirectory, logger);

    public static DriverStatus Ensure(string baseDir, ILogger logger)
    {
        var rid = DriverRid();
        var nodeName = OperatingSystem.IsWindows() ? "node.exe" : "node";
        var driverRoot = Path.Combine(baseDir, ".playwright");
        var nodePath = Path.Combine(driverRoot, "node", rid, nodeName);

        if (File.Exists(nodePath))
        {
            EnsureExecutable(nodePath, logger);
            return DriverStatus.Present;
        }

        logger.LogWarning("Playwright driver missing at {Path}; attempting to restore it.", nodePath);

        var cacheDriverRoot = FindCachedDriverRoot(rid, nodeName);
        if (cacheDriverRoot is null)
        {
            logger.LogError(
                "Playwright Node driver for {Rid} is missing at {Path} and no NuGet cache copy is available " +
                "to restore it — the browser cannot launch, so pages will not render. Rebuild the app " +
                "(dotnet build) or rebuild the packaged bundle so the driver is provisioned.",
                rid,
                nodePath);
            return DriverStatus.Missing;
        }

        try
        {
            // Copy only what the driver needs: the JS package and this RID's node binary (the cache also
            // holds every other RID — copying all of them would move hundreds of MB for nothing).
            CopyDirectory(Path.Combine(cacheDriverRoot, "package"), Path.Combine(driverRoot, "package"));
            CopyDirectory(Path.Combine(cacheDriverRoot, "node", rid), Path.Combine(driverRoot, "node", rid));
            EnsureExecutable(nodePath, logger);

            if (File.Exists(nodePath))
            {
                logger.LogWarning("Restored the Playwright driver from the NuGet cache ({Cache}).", cacheDriverRoot);
                return DriverStatus.Healed;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to restore the Playwright driver from {Cache}", cacheDriverRoot);
        }

        return DriverStatus.Missing;
    }

    private static void EnsureExecutable(string path, ILogger logger)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            var mode = File.GetUnixFileMode(path);
            var want = mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            if (mode != want)
            {
                File.SetUnixFileMode(path, want);
                logger.LogWarning("Restored the executable bit on the Playwright driver: {Path}", path);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not set the executable bit on {Path}", path);
        }
    }

    private static string? FindCachedDriverRoot(string rid, string nodeName)
    {
        // Only the Patchright package: a stock microsoft.playwright driver lacks Patchright's patches.
        // The version the app was built against wins (a driver from another version may not speak the
        // same protocol); the highest cached version is the fallback.
        var wanted = typeof(Microsoft.Playwright.IPlaywright).Assembly.GetName().Version;
        foreach (var packagesRoot in NugetPackageRoots())
        {
            var packageDir = Path.Combine(packagesRoot, "patchright");
            if (!Directory.Exists(packageDir))
            {
                continue;
            }

            var candidates = Directory.EnumerateDirectories(packageDir)
                .OrderByDescending(d => Version.TryParse(Path.GetFileName(d), out var v) && wanted is not null
                    && v.Major == wanted.Major && v.Minor == wanted.Minor && v.Build == wanted.Build)
                .ThenByDescending(d => Version.TryParse(Path.GetFileName(d), out var v) ? v : new Version(0, 0));
            foreach (var versionDir in candidates)
            {
                var driverRoot = Path.Combine(versionDir, ".playwright");
                if (File.Exists(Path.Combine(driverRoot, "node", rid, nodeName)))
                {
                    return driverRoot;
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> NugetPackageRoots()
    {
        var overridePath = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (!string.IsNullOrEmpty(overridePath) && Directory.Exists(overridePath))
        {
            yield return overridePath;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
        {
            var defaultRoot = Path.Combine(home, ".nuget", "packages");
            if (Directory.Exists(defaultRoot))
            {
                yield return defaultRoot;
            }
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        if (!Directory.Exists(source))
        {
            return;
        }

        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static string DriverRid()
    {
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            _ => "x64",
        };

        if (OperatingSystem.IsMacOS())
        {
            return $"darwin-{arch}";
        }

        if (OperatingSystem.IsWindows())
        {
            return "win32_x64";
        }

        return $"linux-{arch}";
    }
}
