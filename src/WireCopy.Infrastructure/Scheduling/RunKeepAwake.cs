// Licensed under the MIT License. See LICENSE in the repository root.

using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace WireCopy.Infrastructure.Scheduling;

/// <summary>
/// workspace-wxll0.3 — asks the operating system to keep the computer awake for as long as a
/// routine run lasts. A run takes minutes to hours (local narration), usually with nobody at the
/// machine; a laptop that dozes off freezes it, and on the Mac a 05:00 run caught up in a brief
/// "dark wake" got a few seconds of work every quarter of an hour (logs, 17-20 Nov 2026).
///
/// The work is done by a small helper program that holds the system awake while it runs:
/// <c>caffeinate -s</c> on macOS (awake on mains power, also in a dark wake; battery keeps its
/// normal rules), <c>systemd-inhibit</c> on Linux. It is started when the run starts and ended
/// when the run ends. Where no helper exists the run still happens and one log line says the
/// computer may sleep under it.
/// </summary>
internal sealed class RunKeepAwake : IDisposable
{
    /// <summary>Replaces the helper command; <c>{pid}</c> becomes this app's process id. Run through <c>sh -c</c>.</summary>
    internal const string CommandVariable = "WIRECOPY_KEEP_AWAKE_COMMAND";

    /// <summary>Set to <c>0</c> to leave the computer's sleep alone.</summary>
    internal const string SwitchVariable = "WIRECOPY_KEEP_AWAKE";

    private readonly ILogger _logger;
    private Process? _helper;

    private RunKeepAwake(ILogger logger, Process? helper)
    {
        _logger = logger;
        _helper = helper;
    }

    /// <summary>Starts the helper for this run. Never throws: a run goes ahead without it.</summary>
    /// <param name="logger">Receives the one line that says what was done.</param>
    /// <returns>The hold; dispose it when the run ends.</returns>
    public static RunKeepAwake Start(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        try
        {
            var (psi, describe, why) = Resolve(Environment.GetEnvironmentVariable, File.Exists, Environment.ProcessId);
            if (psi is null)
            {
                logger.LogInformation("Not keeping this computer awake for the run: {Why:l}. If it sleeps, the run pauses until it wakes", why);
                return new RunKeepAwake(logger, null);
            }

            var helper = Process.Start(psi);
            if (helper is null)
            {
                logger.LogWarning("Could not keep this computer awake for the run ({Command:l} did not start). If it sleeps, the run pauses until it wakes", describe);
                return new RunKeepAwake(logger, null);
            }

            logger.LogInformation("Keeping this computer awake for the run ({Command:l}, process {Pid})", describe, helper.Id);
            return new RunKeepAwake(logger, helper);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            logger.LogWarning("Could not keep this computer awake for the run ({Message:l}). If it sleeps, the run pauses until it wakes", ex.Message);
            return new RunKeepAwake(logger, null);
        }
    }

    /// <summary>Ends the helper, letting the computer sleep again.</summary>
    public void Dispose()
    {
        var helper = Interlocked.Exchange(ref _helper, null);
        if (helper is null)
        {
            return;
        }

        try
        {
            if (!helper.HasExited)
            {
                helper.Kill(entireProcessTree: true);
                helper.WaitForExit(2000);
            }

            _logger.LogInformation("The run is over: this computer may sleep again");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            _logger.LogDebug(ex, "The keep-awake helper could not be ended");
        }
        finally
        {
            helper.Dispose();
        }
    }

    /// <summary>Chooses the helper for this machine.</summary>
    /// <param name="getEnv">Reads an environment variable.</param>
    /// <param name="fileExists">Whether a file exists.</param>
    /// <param name="pid">This app's process id.</param>
    /// <returns>What to start with a short description of it, or null with the reason nothing is started.</returns>
    internal static (ProcessStartInfo? StartInfo, string? Describe, string? Why) Resolve(
        Func<string, string?> getEnv, Func<string, bool> fileExists, int pid)
    {
        if (string.Equals(getEnv(SwitchVariable)?.Trim(), "0", StringComparison.Ordinal))
        {
            return (null, null, $"{SwitchVariable}=0");
        }

        var pidText = pid.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var custom = getEnv(CommandVariable);
        if (!string.IsNullOrWhiteSpace(custom))
        {
            var command = custom.Replace("{pid}", pidText, StringComparison.Ordinal);
            var shell = new ProcessStartInfo("sh") { UseShellExecute = false };
            shell.ArgumentList.Add("-c");
            shell.ArgumentList.Add("exec " + command);
            return (shell, command, null);
        }

        if (OperatingSystem.IsMacOS())
        {
            const string Caffeinate = "/usr/bin/caffeinate";
            if (!fileExists(Caffeinate))
            {
                return (null, null, $"{Caffeinate} is missing");
            }

            // -s: no system sleep while on mains power (holds a dark wake too); -w: ends with this app.
            var psi = new ProcessStartInfo(Caffeinate) { UseShellExecute = false };
            psi.ArgumentList.Add("-s");
            psi.ArgumentList.Add("-w");
            psi.ArgumentList.Add(pidText);
            return (psi, $"caffeinate -s -w {pidText}", null);
        }

        if (OperatingSystem.IsLinux())
        {
            var inhibit = new[] { "/usr/bin/systemd-inhibit", "/bin/systemd-inhibit" }.FirstOrDefault(fileExists);
            if (inhibit is null)
            {
                return (null, null, "this system has no systemd-inhibit");
            }

            var psi = new ProcessStartInfo(inhibit) { UseShellExecute = false };
            foreach (var arg in new[] { "--what=sleep:idle", "--who=WireCopy", "--why=A routine run is in progress", "--mode=block", "sleep", "infinity" })
            {
                psi.ArgumentList.Add(arg);
            }

            return (psi, "systemd-inhibit --what=sleep:idle", null);
        }

        return (null, null, "this system has no helper WireCopy knows");
    }
}
