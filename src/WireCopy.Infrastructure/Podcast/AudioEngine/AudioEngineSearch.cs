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

    /// <summary>Folders that hold build output or dependencies, not the code a build is made from.</summary>
    private static readonly HashSet<string> CodeSkippedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", ".artifacts", "__pycache__", ".venv",
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

    /// <summary>
    /// Every running AudioEngine process whose executable sits in (or under) a folder that probes OK,
    /// with when it started (workspace-wxll0.2). Processes that cannot be read are left out.
    /// </summary>
    /// <param name="probe">Returns the program when a folder holds audio-engine.</param>
    /// <returns>The running engines; empty when none can be identified.</returns>
    internal static IReadOnlyList<RunningEngine> RunningEngines(Func<string, AudioEngineProgram?> probe)
    {
        var found = new List<RunningEngine>();
        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch (Exception)
        {
            return found;
        }

        try
        {
            foreach (var process in processes)
            {
                try
                {
                    if (!process.ProcessName.StartsWith("AudioEngine", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var dir = Path.GetDirectoryName(process.MainModule?.FileName);
                    for (var level = 0; !string.IsNullOrEmpty(dir) && level <= 6; level++)
                    {
                        if (probe(dir) is { } program)
                        {
                            found.Add(new RunningEngine(process.Id, process.StartTime.ToUniversalTime(), program));
                            break;
                        }

                        dir = Path.GetDirectoryName(dir);
                    }
                }
                catch (Exception)
                {
                    // Access denied or exited: try the next process.
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

        return found;
    }

    /// <summary>
    /// The processes listening on a local TCP port, asked of <c>lsof</c> (macOS, most Linux) or
    /// <c>fuser</c>. Tells which of several running engines is the one behind an address.
    /// </summary>
    /// <param name="port">The port.</param>
    /// <returns>The process ids; null when neither tool is on this machine or it would not answer.</returns>
    internal static IReadOnlySet<int>? ListenerPids(int port)
    {
        var portText = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (var (tool, args) in new[]
                 {
                     ("lsof", new[] { "-nP", "-iTCP:" + portText, "-sTCP:LISTEN", "-t" }),
                     ("fuser", new[] { portText + "/tcp" }),
                 })
        {
            try
            {
                var psi = new ProcessStartInfo(tool) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var arg in args)
                {
                    psi.ArgumentList.Add(arg);
                }

                using var process = Process.Start(psi);
                if (process is null)
                {
                    continue;
                }

                var output = process.StandardOutput.ReadToEndAsync();
                _ = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(3000))
                {
                    process.Kill();
                    continue;
                }

                var pids = new HashSet<int>();
                foreach (var token in output.GetAwaiter().GetResult().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (int.TryParse(token, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var pid))
                    {
                        pids.Add(pid);
                    }
                }

                return pids;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                // This tool is not here: try the next one.
            }
        }

        return null;
    }

    /// <summary>
    /// When the code a program runs from last changed: for a source checkout the newest file under
    /// <c>src</c> and <c>tools</c> (build output left out), for a published build its own binaries.
    /// </summary>
    /// <param name="program">The program as discovery describes it.</param>
    /// <returns>The newest change in UTC; null when nothing could be read.</returns>
    internal static DateTime? CodeChangedUtc(AudioEngineProgram program)
    {
        try
        {
            DateTime? newest = null;
            void Note(string file)
            {
                var at = File.GetLastWriteTimeUtc(file);
                if (newest is null || at > newest)
                {
                    newest = at;
                }
            }

            if (program.Arguments.Count == 0)
            {
                Note(program.Executable);
                var dll = Path.ChangeExtension(program.Executable, null) + ".dll";
                if (File.Exists(dll))
                {
                    Note(dll);
                }

                return newest;
            }

            var pending = new Stack<string>();
            foreach (var top in new[] { "src", "tools" })
            {
                var root = Path.Combine(program.WorkingDirectory, top);
                if (Directory.Exists(root))
                {
                    pending.Push(root);
                }
            }

            var listed = 0;
            while (pending.Count > 0 && listed++ < WalkMaxListings)
            {
                var folder = pending.Pop();
                foreach (var file in Directory.EnumerateFiles(folder))
                {
                    Note(file);
                }

                foreach (var sub in Directory.EnumerateDirectories(folder))
                {
                    var name = Path.GetFileName(sub);
                    if (!CodeSkippedFolders.Contains(name))
                    {
                        pending.Push(sub);
                    }
                }
            }

            return newest;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
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
