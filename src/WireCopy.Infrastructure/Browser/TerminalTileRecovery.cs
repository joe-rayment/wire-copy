// Licensed under the MIT License. See LICENSE in the repository root.

using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace WireCopy.Infrastructure.Browser;

/// <summary>
/// workspace-9k27.6: crash-safe record of a side-by-side terminal tile so the
/// user's terminal is never left permanently shrunken. <see cref="BrowserSession"/>
/// writes a record when it tiles the terminal and clears it on restore; if the app
/// crashes (or is killed) while docked, the record survives on disk and startup
/// recovery puts the terminal back.
///
/// The restore is CONDITIONAL: it only fires when the terminal's current bounds
/// still match the tile we set (within a small tolerance) — if the user has moved
/// or resized the window since, their arrangement wins and the record is simply
/// discarded.
/// </summary>
public static class TerminalTileRecovery
{
    /// <summary>Tolerance in px when comparing the live window to the recorded tile.</summary>
    internal const int MatchTolerancePx = 8;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    internal static string DefaultRecordPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WireCopy",
        "terminal-tile.json");

    /// <summary>
    /// Startup recovery entry point (macOS only): self-contained osascript
    /// runner so this can be called before any <see cref="BrowserSession"/> exists.
    /// No-op (fast, no process spawn) when no record file is present.
    /// </summary>
    public static Task RecoverAtStartupAsync(ILogger logger, string? path = null)
    {
        if (!OperatingSystem.IsMacOS() || TryRead(path) is null)
        {
            return Task.CompletedTask;
        }

        return RecoverAsync(RunOsascriptAsync, logger, path);
    }

    /// <summary>Persists the pre-dock bounds + the tile we set. Best-effort.</summary>
    internal static void Record(
        string bundleId,
        TerminalTiling.WindowRect preDock,
        TerminalTiling.WindowRect tile,
        ILogger logger,
        string? path = null)
    {
        try
        {
            var file = path ?? DefaultRecordPath;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var record = new TileRecord(
                bundleId,
                preDock.X,
                preDock.Y,
                preDock.Width,
                preDock.Height,
                tile.X,
                tile.Y,
                tile.Width,
                tile.Height);
            File.WriteAllText(file, JsonSerializer.Serialize(record, JsonOptions));
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not persist the terminal tile record (crash recovery unavailable this session)");
        }
    }

    /// <summary>Removes the record after a successful (or declined) restore.</summary>
    internal static void Clear(ILogger logger, string? path = null)
    {
        try
        {
            var file = path ?? DefaultRecordPath;
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not delete the terminal tile record");
        }
    }

    /// <summary>Reads a leftover record, or null when none/corrupt.</summary>
    internal static TileRecord? TryRead(string? path = null)
    {
        try
        {
            var file = path ?? DefaultRecordPath;
            if (!File.Exists(file))
            {
                return null;
            }

            return JsonSerializer.Deserialize<TileRecord>(File.ReadAllText(file));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Startup recovery (macOS): if a record was left behind by a crash while
    /// docked, restore the terminal's pre-dock bounds. The osascript itself
    /// finds the window still sitting ON the recorded tile (workspace-9k27.6
    /// minor: never <c>window 1</c>, which in a multi-window terminal may be a
    /// different window) and restores exactly that one; when no window matches
    /// — the user moved/resized/closed it — nothing is touched. Always clears
    /// the record.
    /// </summary>
    internal static async Task RecoverAsync(
        Func<string, Task<(int ExitCode, string StdOut, string StdErr)?>> runOsascript,
        ILogger logger,
        string? path = null)
    {
        var record = TryRead(path);
        if (record is null)
        {
            return;
        }

        try
        {
            var tile = new TerminalTiling.WindowRect(record.TileX, record.TileY, record.TileW, record.TileH);
            var preDock = new TerminalTiling.WindowRect(record.PreX, record.PreY, record.PreW, record.PreH);
            var result = await runOsascript(TerminalTiling.BuildRestoreMatchedWindowScript(
                record.BundleId, tile, preDock, MatchTolerancePx)).ConfigureAwait(false);

            if (result is { ExitCode: 0 } r)
            {
                var verdict = r.StdOut.Trim();
                if (verdict == TerminalTiling.RestoreResultRestored)
                {
                    logger.LogInformation(
                        "Recovered the terminal window from a previous session's tile ({TileW}x{TileH} → {PreW}x{PreH})",
                        record.TileW.ToString(CultureInfo.InvariantCulture),
                        record.TileH.ToString(CultureInfo.InvariantCulture),
                        record.PreW.ToString(CultureInfo.InvariantCulture),
                        record.PreH.ToString(CultureInfo.InvariantCulture));
                }
                else
                {
                    logger.LogInformation(
                        "Terminal tile recovery: no window still matches the recorded tile — keeping the user's layout");
                }
            }
            else
            {
                logger.LogWarning("Terminal tile recovery could not restore the window (osascript failed)");
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Terminal tile recovery failed (non-fatal)");
        }
        finally
        {
            Clear(logger, path);
        }
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)?> RunOsascriptAsync(string script)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "/usr/bin/osascript",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add(script);

            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null)
            {
                return null;
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            return (process.ExitCode, await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false));
        }
        catch
        {
            return null;
        }
    }

    internal sealed record TileRecord(
        string BundleId,
        int PreX,
        int PreY,
        int PreW,
        int PreH,
        int TileX,
        int TileY,
        int TileW,
        int TileH);
}
