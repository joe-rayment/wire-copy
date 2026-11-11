// Licensed under the MIT License. See LICENSE in the repository root.

using System.Diagnostics;

namespace WireCopy.Infrastructure.Podcast.AudioEngine;

/// <summary>Bounded, error-swallowing searches for an audio-engine folder (home walk, macOS Spotlight).</summary>
internal static class AudioEngineSearch
{
    /// <summary>Directory listings allowed for the home walk.</summary>
    internal const int WalkMaxListings = 4000;

    /// <summary>Wall-clock budget for the home walk.</summary>
    internal static readonly TimeSpan WalkBudget = TimeSpan.FromMilliseconds(1500);

    /// <summary>Folders never listed: noisy, or (on macOS) listing them raises a permission prompt.</summary>
    private static readonly HashSet<string> SkippedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Library", "node_modules", "bin", "obj", "Documents", "Desktop", "Downloads", "Pictures", "Music", "Movies",
    };

    /// <summary>Lists the real subfolders of a folder, skipping symlinked ones; empty on any error.</summary>
    /// <param name="folder">The folder to list.</param>
    /// <returns>Full paths of subfolders.</returns>
    internal static IEnumerable<string> ListDirectories(string folder)
    {
        var result = new List<string>();
        try
        {
            foreach (var dir in new DirectoryInfo(folder).EnumerateDirectories())
            {
                try
                {
                    if (dir.LinkTarget is null && !dir.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        result.Add(dir.FullName);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Skip this entry.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable folder: nothing to offer.
        }

        return result;
    }

    /// <summary>Whether a folder name looks like an audio-engine folder.</summary>
    /// <param name="name">The folder's name.</param>
    /// <returns>True for <c>audio-engine</c>, <c>AudioEngine</c> or <c>audio-engine-*</c> (any case).</returns>
    internal static bool LooksLikeEngine(string name)
        => name.Equals("audio-engine", StringComparison.OrdinalIgnoreCase)
           || name.Equals("AudioEngine", StringComparison.OrdinalIgnoreCase)
           || name.StartsWith("audio-engine-", StringComparison.OrdinalIgnoreCase);

    /// <summary>Walks <paramref name="root"/> breadth-first to depth 3 for a folder that is an audio-engine.</summary>
    /// <param name="root">The folder to start in (the home folder).</param>
    /// <param name="listDirs">Lists subfolders of a folder.</param>
    /// <param name="probe">Returns the program when a folder holds audio-engine.</param>
    /// <param name="budget">Wall-clock limit.</param>
    /// <param name="maxListings">Directory-listing limit.</param>
    /// <returns>The first program found, or null.</returns>
    internal static AudioEngineProgram? WalkFor(
        string root,
        Func<string, IEnumerable<string>> listDirs,
        Func<string, AudioEngineProgram?> probe,
        TimeSpan budget,
        int maxListings)
    {
        var clock = Stopwatch.StartNew();
        var listings = 0;
        var level = new List<string> { root };
        for (var depth = 1; depth <= 3 && level.Count > 0; depth++)
        {
            var next = new List<string>();
            foreach (var folder in level)
            {
                if (clock.Elapsed > budget || listings >= maxListings)
                {
                    return null;
                }

                listings++;
                IEnumerable<string> children;
                try
                {
                    children = listDirs(folder).ToList();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                foreach (var child in children)
                {
                    var name = Path.GetFileName(child.TrimEnd('/', '\\'));
                    if (name.Length == 0 || name[0] == '.' || SkippedNames.Contains(name))
                    {
                        continue;
                    }

                    if (LooksLikeEngine(name) && probe(child) is { } program)
                    {
                        return program;
                    }

                    next.Add(child);
                }
            }

            level = next;
        }

        return null;
    }

    /// <summary>macOS only: Spotlight's idea of where <c>AudioEngine.Server</c> lives.</summary>
    /// <returns>Up to 50 result paths; empty on any failure or after 3 s.</returns>
    internal static IEnumerable<string> SpotlightCandidates()
    {
        var lines = new List<string>();
        try
        {
            var psi = new ProcessStartInfo("mdfind")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-name");
            psi.ArgumentList.Add("AudioEngine.Server");
            using var process = Process.Start(psi);
            if (process is null)
            {
                return lines;
            }

            var reader = Task.Run(() =>
            {
                string? line;
                while (lines.Count < 50 && (line = process.StandardOutput.ReadLine()) is not null)
                {
                    lock (lines)
                    {
                        lines.Add(line);
                    }
                }
            });
            if (!process.WaitForExit(3000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Already gone.
                }
            }

            reader.Wait(500);
        }
        catch (Exception)
        {
            // mdfind missing or failing: no candidates.
        }

        lock (lines)
        {
            return lines.Where(l => !string.IsNullOrWhiteSpace(l)).Take(50).ToList();
        }
    }

    /// <summary>Finds the folder of a running AudioEngine process that probes OK.</summary>
    /// <param name="probe">Returns the program when a folder holds audio-engine.</param>
    /// <returns>The folder to remember, or null.</returns>
    internal static string? RunningEngineFolder(Func<string, AudioEngineProgram?> probe)
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch (Exception)
        {
            return null;
        }

        try
        {
            foreach (var process in processes)
            {
                string? exe = null;
                try
                {
                    if (process.ProcessName.StartsWith("AudioEngine", StringComparison.OrdinalIgnoreCase))
                    {
                        exe = process.MainModule?.FileName;
                    }
                }
                catch (Exception)
                {
                    // Access denied or exited: try the next process.
                }

                var dir = string.IsNullOrEmpty(exe) ? null : Path.GetDirectoryName(exe);
                for (var level = 0; dir is not null && level <= 6; level++)
                {
                    if (probe(dir) is not null)
                    {
                        return dir;
                    }

                    dir = Path.GetDirectoryName(dir);
                }
            }
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }

        return null;
    }
}
