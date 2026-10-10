// Licensed under the MIT License. See LICENSE in the repository root.

using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WireCopy.Infrastructure.Configuration;
using WireCopy.Infrastructure.Podcast;
using Xunit;

namespace WireCopy.Tests.Podcast;

/// <summary>
/// workspace-skoug: process-handling tests for M4bAudioAssembler's ffmpeg spawns.
/// ffmpeg is resolved through PATH, so these tests point PATH at a temp directory holding a
/// stand-in <c>ffmpeg</c> shell script (or at an empty directory for the "missing" case).
/// PATH is process-global, hence the serialized <see cref="ProcessGlobalStateCollection"/>.
/// </summary>
[Collection(ProcessGlobalStateCollection.Name)]
[Trait("Category", "Unit")]
public sealed class M4bAudioAssemblerProcessTests : IDisposable
{
    private readonly M4bAudioAssembler _sut;
    private readonly string _dir;
    private readonly string? _originalPath;

    public M4bAudioAssemblerProcessTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"m4b-proc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _originalPath = Environment.GetEnvironmentVariable("PATH");
        _sut = new M4bAudioAssembler(
            Options.Create(new PodcastConfiguration { TempDirectory = _dir }),
            NullLogger<M4bAudioAssembler>.Instance);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PATH", _originalPath);
        try { Directory.Delete(_dir, true); }
        catch { /* best-effort cleanup */ }
    }

    // Guards:   backing out (cancelling) while checking for ffmpeg surfaces as a cancellation,
    //           not as a false "ffmpeg is not installed" answer.
    // Fails if: ValidatePrerequisitesAsync swallows OperationCanceledException (the old bare
    //           catch) and returns false instead of throwing.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~ValidatePrerequisitesAsync_AlreadyCancelledToken_Throws"
    [Fact]
    public async Task ValidatePrerequisitesAsync_AlreadyCancelledToken_Throws()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => _sut.ValidatePrerequisitesAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // Guards:   when no ffmpeg binary exists, ValidatePrerequisitesAsync still answers false
    //           (the "not installed" case) rather than throwing.
    // Fails if: the missing-binary Win32Exception escapes instead of being turned into false.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~ValidatePrerequisitesAsync_MissingBinary_ReturnsFalse"
    [Fact]
    public async Task ValidatePrerequisitesAsync_MissingBinary_ReturnsFalse()
    {
        var emptyDir = Path.Combine(_dir, "empty");
        Directory.CreateDirectory(emptyDir);
        Environment.SetEnvironmentVariable("PATH", emptyDir);

        var result = await _sut.ValidatePrerequisitesAsync();

        result.Should().BeFalse();
    }

    // Guards:   cancelling while ffmpeg is running kills ffmpeg and its children, so no
    //           orphaned process keeps running (or writing its temp file) after a cancel.
    // Fails if: the cancel path only disposes the Process handle without Kill(entireProcessTree).
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~ValidatePrerequisitesAsync_CancelWhileRunning_KillsProcessTree"
    [SkippableFact]
    public async Task ValidatePrerequisitesAsync_CancelWhileRunning_KillsProcessTree()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "uses a /bin/sh stand-in and /proc");

        var parentPidFile = Path.Combine(_dir, "parent.pid");
        var childPidFile = Path.Combine(_dir, "child.pid");
        InstallFakeFfmpeg(
            $"echo $$ > '{parentPidFile}'\nsleep 300 &\necho $! > '{childPidFile}'\nwait\n");

        using var cts = new CancellationTokenSource();
        var running = _sut.ValidatePrerequisitesAsync(cts.Token);

        int parentPid = 0, childPid = 0;
        try
        {
            (await WaitUntilAsync(
                () => TryReadPid(parentPidFile, out parentPid) && TryReadPid(childPidFile, out childPid),
                TimeSpan.FromSeconds(10)))
                .Should().BeTrue("the stand-in ffmpeg should have started and recorded its pids");
            IsAlive(parentPid).Should().BeTrue();
            IsAlive(childPid).Should().BeTrue();

            await cts.CancelAsync();
            await FluentActions.Awaiting(() => running).Should().ThrowAsync<OperationCanceledException>();

            (await WaitUntilAsync(() => !IsAlive(parentPid) && !IsAlive(childPid), TimeSpan.FromSeconds(5)))
                .Should().BeTrue("cancelling must kill ffmpeg and everything it spawned");
        }
        finally
        {
            // Never leak the 300s sleep if the assertion above failed.
            KillQuietly(parentPid);
            KillQuietly(childPid);
        }
    }

    // Guards:   ffmpeg's stdout is drained while it runs, so a chatty process cannot fill the
    //           pipe and hang forever.
    // Fails if: stdout is redirected but never read (the old code) - the stand-in blocks on a
    //           full pipe and the call times out instead of returning true.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~ValidatePrerequisitesAsync_ChattyStdout_DoesNotHang"
    [SkippableFact]
    public async Task ValidatePrerequisitesAsync_ChattyStdout_DoesNotHang()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "uses a /bin/sh stand-in");

        // 1 MB on stdout, far beyond the ~64 KB pipe buffer.
        InstallFakeFfmpeg("head -c 1000000 /dev/zero | tr '\\0' 'x'\nexit 0\n");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var result = await _sut.ValidatePrerequisitesAsync(timeout.Token);

        result.Should().BeTrue();
    }

    private void InstallFakeFfmpeg(string scriptBody)
    {
        var binDir = Path.Combine(_dir, "bin");
        Directory.CreateDirectory(binDir);
        var script = Path.Combine(binDir, "ffmpeg");
        File.WriteAllText(script, "#!/bin/sh\n" + scriptBody);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                script,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Environment.SetEnvironmentVariable("PATH", binDir + Path.PathSeparator + _originalPath);
    }

    private static bool TryReadPid(string file, out int pid)
    {
        pid = 0;
        try
        {
            return File.Exists(file) && int.TryParse(File.ReadAllText(file).Trim(), out pid) && pid > 0;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>A process counts as dead once /proc/&lt;pid&gt; is gone or it is a zombie awaiting reaping.</summary>
    private static bool IsAlive(int pid)
    {
        try
        {
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            var state = stat[(stat.LastIndexOf(')') + 2)..].FirstOrDefault();
            return state != 'Z' && state != 'X';
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static void KillQuietly(int pid)
    {
        if (pid <= 0)
        {
            return;
        }

        try
        {
            using var p = Process.GetProcessById(pid);
            p.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // Already gone.
        }
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(50);
        }

        return condition();
    }
}
