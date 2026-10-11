// Licensed under the MIT License. See LICENSE in the repository root.

using System.Diagnostics;

namespace WireCopy.Infrastructure.BrowserExtension;

/// <summary>
/// Small "do the next step for the user" helpers for the Settings card (workspace-m7aml): open a
/// browser's extensions page and reveal the exported extension folder. Never throws; each returns
/// whether the process started.
/// </summary>
public static class BrowserExtensionLauncher
{
#pragma warning disable S1075 // The browser-internal page is a fixed scheme, not a configurable URL.
    private const string ExtensionsUrl = "chrome://extensions";
#pragma warning restore S1075

    /// <summary>Opens <c>chrome://extensions</c> in the given browser, detached from this app.</summary>
    /// <param name="browser">A registration with a <see cref="BrowserRegistration.LaunchExecutable"/>.</param>
    /// <returns>True when the browser process was started.</returns>
    public static bool OpenExtensionsPage(BrowserRegistration browser)
    {
        if (string.IsNullOrEmpty(browser.LaunchExecutable))
        {
            return false;
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                var psi = new ProcessStartInfo(browser.LaunchExecutable)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                psi.ArgumentList.Add(ExtensionsUrl);
                return Start(psi);
            }

            return StartDetachedUnix(browser.LaunchExecutable, ExtensionsUrl);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Shows <paramref name="directory"/> in the system file manager.</summary>
    /// <param name="directory">The folder to reveal.</param>
    /// <returns>True when the file-manager process was started.</returns>
    public static bool RevealFolder(string directory)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return Start(new ProcessStartInfo("explorer", "/select,\"" + directory + "\"") { UseShellExecute = false });
            }

            return OperatingSystem.IsMacOS()
                ? StartDetachedUnix("open", "-R", directory)
                : StartDetachedUnix("xdg-open", directory);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Starts a program with stdio pointed at /dev/null so it outlives this app without a
    /// terminal attached: <c>sh -c 'exec "$@" &lt;/dev/null &gt;/dev/null 2&gt;&amp;1' sh exe args…</c>.
    /// </summary>
    private static bool StartDetachedUnix(string executable, params string[] args)
    {
        var psi = new ProcessStartInfo("sh") { UseShellExecute = false };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("exec \"$@\" </dev/null >/dev/null 2>&1");
        psi.ArgumentList.Add("sh");
        psi.ArgumentList.Add(executable);
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        return Start(psi);
    }

    private static bool Start(ProcessStartInfo psi)
    {
        using var process = Process.Start(psi);
        return process is not null;
    }
}
