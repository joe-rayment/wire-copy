// Licensed under the MIT License. See LICENSE in the repository root.

using System.Diagnostics;
using System.Globalization;

namespace WireCopy.Infrastructure.BrowserExtension;

/// <summary>
/// Works out which browser launched the native-messaging host (workspace-m7aml), so the
/// Settings card can show "connected" per browser. Best effort: any failure is "browser".
/// </summary>
internal static class ParentBrowser
{
    /// <summary>The id used when the launcher cannot be identified.</summary>
    public const string Unknown = "browser";

    /// <summary>Identifies the browser that started this process; never throws.</summary>
    /// <returns>"chrome", "chromium", "vivaldi", "brave", "edge" or "browser".</returns>
    public static string Detect()
    {
        try
        {
            string? exePath = null;
            if (OperatingSystem.IsLinux())
            {
                exePath = LinuxParentExecutable();
            }
            else if (OperatingSystem.IsMacOS())
            {
                exePath = MacParentExecutable();
            }

            return FromExecutablePath(exePath);
        }
        catch (Exception)
        {
            return Unknown;
        }
    }

    /// <summary>Maps a browser executable path (or macOS process name) to a browser id.</summary>
    /// <param name="exePath">The parent process's executable path, or null when unknown.</param>
    /// <returns>The browser id, or "browser" when nothing matches.</returns>
    public static string FromExecutablePath(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return Unknown;
        }

        // Order matters: Brave and Edge ship Chromium inside, and a path may mention several names.
        if (Contains(exePath, "vivaldi"))
        {
            return "vivaldi";
        }

        if (Contains(exePath, "brave"))
        {
            return "brave";
        }

        if (Contains(exePath, "msedge") || Contains(exePath, "microsoft-edge") || Contains(exePath, "microsoft edge"))
        {
            return "edge";
        }

        if (Contains(exePath, "chromium"))
        {
            return "chromium";
        }

        return Contains(exePath, "chrome") ? "chrome" : Unknown;
    }

    private static bool Contains(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static string? LinuxParentExecutable()
    {
        // /proc/self/stat is "pid (comm) state ppid ..."; comm may contain spaces and
        // parentheses, so split after the LAST ')'.
        var stat = File.ReadAllText("/proc/self/stat");
        var close = stat.LastIndexOf(')');
        if (close < 0)
        {
            return null;
        }

        var fields = stat[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 2 || !int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ppid))
        {
            return null;
        }

        return new FileInfo($"/proc/{ppid}/exe").LinkTarget;
    }

    private static string? MacParentExecutable()
    {
        var ppid = RunPs("-o", "ppid=", "-p", Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        return string.IsNullOrWhiteSpace(ppid) ? null : RunPs("-o", "comm=", "-p", ppid);
    }

    private static string? RunPs(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("ps")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return null;
        }

        var output = process.StandardOutput.ReadToEnd().Trim();
        if (!process.WaitForExit(2000))
        {
            process.Kill();
            return null;
        }

        return output;
    }
}
