// Licensed under the MIT License. See LICENSE in the repository root.

using System.Diagnostics;

namespace WireCopy.Infrastructure.BrowserExtension;

/// <summary>
/// Small "do the next step for the user" helpers for the Settings card (workspace-m7aml): open the local
/// "Add Wire Copy" guide page in a browser and reveal the exported extension folder. Never throws; each
/// returns whether the process started.
/// </summary>
public static class BrowserExtensionLauncher
{
    /// <summary>
    /// The <c>file:</c> URL of the guide page with the browser name and extension folder in its query.
    /// Chromium drops <c>chrome://</c> URLs passed on the command line but lets <c>file:</c> ones through,
    /// so this page (not <c>chrome://extensions</c>) is what the browser is launched on.
    /// </summary>
    /// <param name="guidePath">Absolute path of <c>add-extension.html</c>.</param>
    /// <param name="browserName">Short browser name shown in the page title.</param>
    /// <param name="folder">Absolute path of the exported extension folder.</param>
    /// <returns>An absolute <c>file:</c> URL; the query values are percent-escaped.</returns>
    public static string BuildGuideUrl(string guidePath, string browserName, string folder) =>
        new Uri(Path.GetFullPath(guidePath)).AbsoluteUri
        + "?browser=" + Uri.EscapeDataString(browserName)
        + "&folder=" + Uri.EscapeDataString(folder);

    /// <summary>Opens the guide page in the given browser, detached from this app.</summary>
    /// <param name="browser">A registration with a <see cref="BrowserRegistration.LaunchExecutable"/>.</param>
    /// <param name="browserName">Short browser name shown in the page title.</param>
    /// <param name="guidePath">Absolute path of <c>add-extension.html</c>.</param>
    /// <param name="folder">Absolute path of the exported extension folder.</param>
    /// <returns>True when the browser process was started.</returns>
    public static bool OpenGuidePage(BrowserRegistration browser, string browserName, string guidePath, string folder)
    {
        if (string.IsNullOrEmpty(browser.LaunchExecutable))
        {
            return false;
        }

        try
        {
            var url = BuildGuideUrl(guidePath, browserName, folder);
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
                psi.ArgumentList.Add(url);
                return Start(psi);
            }

            return StartDetachedUnix(browser.LaunchExecutable, url);
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
