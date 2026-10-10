// Licensed under the MIT License. See LICENSE in the repository root.

using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WireCopy.Application.DTOs.Audio;
using WireCopy.Domain.ValueObjects.Audio;
using WireCopy.Infrastructure.Configuration;
using WireCopy.Infrastructure.Podcast;
using WireCopy.Infrastructure.Podcast.Chatterbox;
using Xunit;

namespace WireCopy.Tests.Podcast;

/// <summary>
/// workspace-vfnxw: the three FFMpegCore concat/transcode passes (M4B assembly, OpenAI chunk
/// concat, Chatterbox chunk transcode) must stop ffmpeg when the caller cancels. ffmpeg is
/// resolved through PATH, so each test puts a stand-in <c>ffmpeg</c> shell script on PATH: it
/// answers <c>-version</c> (FFMpegCore runs that once per process) and otherwise records its
/// pid and <c>exec</c>s <c>sleep 300</c> (exec, so no child keeps stdout open and hangs
/// FFMpegCore). PATH is process-global, hence the serialized
/// <see cref="ProcessGlobalStateCollection"/>.
/// </summary>
[Collection(ProcessGlobalStateCollection.Name)]
[Trait("Category", "Unit")]
public sealed class FfmpegConcatCancellationTests : IDisposable
{
    private readonly string _dir;
    private readonly string? _originalPath;
    private readonly string _pidFile;

    public FfmpegConcatCancellationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"ffmpeg-cancel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _originalPath = Environment.GetEnvironmentVariable("PATH");
        _pidFile = Path.Combine(_dir, "ffmpeg.pid");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PATH", _originalPath);
        try { Directory.Delete(_dir, true); }
        catch { /* best-effort cleanup */ }
    }

    // Guards:   cancelling while the M4B assembler's concat pass runs stops ffmpeg (instead of
    //           letting it finish writing the whole file) and surfaces as a cancellation.
    // Fails if: the concat call drops .CancellableThrough(token, ...) - the stand-in ffmpeg
    //           keeps sleeping, so the call neither throws nor ends within the 10 s watchdog.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~M4bAssemble_CancelWhileConcatRuns_StopsFfmpegAndThrowsCancelled"
    [SkippableFact]
    public async Task M4bAssemble_CancelWhileConcatRuns_StopsFfmpegAndThrowsCancelled()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "uses a /bin/sh stand-in and /proc");
        InstallFakeFfmpeg();

        var segmentFile = Path.Combine(_dir, "segment.aac");
        await File.WriteAllBytesAsync(segmentFile, [0, 1, 2, 3]);
        var request = new AssemblyRequest
        {
            Segments =
            [
                new ArticleAudioSegment
                {
                    Title = "Chapter 1",
                    AudioFilePath = segmentFile,
                    Duration = TimeSpan.FromMinutes(1), // non-zero: skips FFProbe
                },
            ],
            Metadata = new AudioMetadata { Title = "Test Podcast Episode" },
            OutputPath = Path.Combine(_dir, "out", "output.m4b"),
        };
        var sut = new M4bAudioAssembler(
            Options.Create(new PodcastConfiguration { TempDirectory = _dir }),
            NullLogger<M4bAudioAssembler>.Instance);

        using var cts = new CancellationTokenSource();
        await AssertCancelStopsFfmpegAsync(() => sut.AssembleAsync(request, null, cts.Token), cts);
    }

    // Guards:   cancelling while OpenAiTtsService concatenates its audio chunks stops ffmpeg and
    //           surfaces as a cancellation.
    // Fails if: ConcatenateWithFfmpegAsync drops .CancellableThrough(token, ...) - the stand-in
    //           ffmpeg keeps sleeping, so the call neither throws nor ends within the watchdog.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~OpenAiConcat_CancelWhileFfmpegRuns_StopsFfmpegAndThrowsCancelled"
    [SkippableFact]
    public async Task OpenAiConcat_CancelWhileFfmpegRuns_StopsFfmpegAndThrowsCancelled()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "uses a /bin/sh stand-in and /proc");
        InstallFakeFfmpeg();

        var sut = new OpenAiTtsService(
            Options.Create(new OpenAiTtsConfiguration { ApiKey = "sk-test-key-123" }),
            NullLogger<OpenAiTtsService>.Instance);

        using var cts = new CancellationTokenSource();
        await AssertCancelStopsFfmpegAsync(
            () => sut.ConcatenateWithFfmpegAsync([[1, 2, 3], [4, 5, 6]], cts.Token),
            cts);
    }

    // Guards:   cancelling while Chatterbox transcodes its per-chunk WAVs to AAC stops ffmpeg and
    //           surfaces as a cancellation.
    // Fails if: AssembleToAacAsync drops .CancellableThrough(token, ...) - the stand-in ffmpeg
    //           keeps sleeping, so the call neither throws nor ends within the watchdog.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~ChatterboxAssemble_CancelWhileFfmpegRuns_StopsFfmpegAndThrowsCancelled"
    [SkippableFact]
    public async Task ChatterboxAssemble_CancelWhileFfmpegRuns_StopsFfmpegAndThrowsCancelled()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "uses a /bin/sh stand-in and /proc");
        InstallFakeFfmpeg();

        var wavs = new List<string>();
        for (var i = 0; i < 2; i++)
        {
            var wav = Path.Combine(_dir, $"chunk-{i}.wav");
            await File.WriteAllBytesAsync(wav, [0, 1, 2, 3]);
            wavs.Add(wav);
        }

        using var tempManager = new TempFileManager(_dir, NullLogger.Instance);
        using var cts = new CancellationTokenSource();
        await AssertCancelStopsFfmpegAsync(
            () => ChatterboxTtsService.AssembleToAacAsync(tempManager, wavs, cts.Token),
            cts);
    }

    /// <summary>
    /// Starts <paramref name="call"/>, waits for the stand-in ffmpeg to record its pid, cancels,
    /// and asserts the call ends with OperationCanceledException and the process is gone. A
    /// watchdog turns "cancel is ignored" into a failure instead of a hang.
    /// </summary>
    private async Task AssertCancelStopsFfmpegAsync(Func<Task> call, CancellationTokenSource cts)
    {
        var pid = 0;
        try
        {
            var running = call();

            (await WaitUntilAsync(() => TryReadPid(_pidFile, out pid), TimeSpan.FromSeconds(15)))
                .Should().BeTrue("the stand-in ffmpeg should have started and recorded its pid");
            IsAlive(pid).Should().BeTrue();

            await cts.CancelAsync();

            var finished = await Task.WhenAny(running, Task.Delay(TimeSpan.FromSeconds(10)));
            finished.Should().BeSameAs(running, "cancelling must end the call, not leave ffmpeg running");
            await FluentActions.Awaiting(() => running).Should().ThrowAsync<OperationCanceledException>();

            (await WaitUntilAsync(() => !IsAlive(pid), TimeSpan.FromSeconds(5)))
                .Should().BeTrue("cancelling must stop the ffmpeg process");
        }
        finally
        {
            // Never leak the 300s sleep if an assertion above failed.
            KillQuietly(pid);
        }
    }

    private void InstallFakeFfmpeg()
    {
        var binDir = Path.Combine(_dir, "bin");
        Directory.CreateDirectory(binDir);
        var script = Path.Combine(binDir, "ffmpeg");
        File.WriteAllText(
            script,
            "#!/bin/sh\n"
            + "case \"$1\" in -version) exit 0;; esac\n"
            + $"echo $$ > '{_pidFile}'\n"
            + "exec sleep 300\n");
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
