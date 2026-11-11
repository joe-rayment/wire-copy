// Licensed under the MIT License. See LICENSE in the repository root.

using System.Globalization;
using WireCopy.Application.Interfaces;

namespace WireCopy.Infrastructure.Scheduling;

/// <summary>
/// workspace-b6cmm — per-run log file for a routine run (what the run did, in order),
/// kept under the app data root so the UI can show why a run failed. Best-effort:
/// nothing here ever throws into the run.
/// </summary>
internal static class RoutineRunLog
{
    private const int MaxLines = 400;
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);

    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WireCopy",
        "routine-runs");

    /// <summary>The log file path for a run.</summary>
    public static string PathFor(Guid runId) => Path.Combine(Dir, runId.ToString("N") + ".log");

    /// <summary>Writes the last 400 lines of the given records, and prunes logs older than 30 days.</summary>
    public static void Write(Guid runId, IEnumerable<LogRecord> records)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var lines = new List<string>();
            foreach (var r in records)
            {
                lines.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{r.Timestamp.ToLocalTime():HH:mm:ss} {Abbrev(r.Level)} {r.Message}"));
                var ex = r.Exception?.Split('\n', 2)[0].TrimEnd('\r');
                if (!string.IsNullOrWhiteSpace(ex))
                {
                    lines.Add("         " + ex);
                }
            }

            var kept = lines.Count > MaxLines ? lines.GetRange(lines.Count - MaxLines, MaxLines) : lines;
            File.WriteAllLines(PathFor(runId), kept);
            Prune();
        }
        catch (Exception)
        {
            // best-effort by contract
        }
    }

    /// <summary>The run's log lines; empty when missing or unreadable.</summary>
    public static IReadOnlyList<string> Read(Guid runId)
    {
        try
        {
            var path = PathFor(runId);
            return File.Exists(path) ? File.ReadAllLines(path) : Array.Empty<string>();
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    private static void Prune()
    {
        var cutoff = DateTime.UtcNow - MaxAge;
        foreach (var f in Directory.EnumerateFiles(Dir, "*.log"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(f) < cutoff)
                {
                    File.Delete(f);
                }
            }
            catch (IOException)
            {
                // skip locked files
            }
        }
    }

    private static string Abbrev(LogSeverity level) => level switch
    {
        LogSeverity.Trace => "TRC",
        LogSeverity.Debug => "DBG",
        LogSeverity.Information => "INF",
        LogSeverity.Warning => "WRN",
        LogSeverity.Error => "ERR",
        _ => "CRT",
    };
}
