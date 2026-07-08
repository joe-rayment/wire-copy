// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WireCopy.Infrastructure.Configuration;
using WireCopy.Infrastructure.Podcast.Chatterbox;
using Xunit;

namespace WireCopy.Tests.Podcast;

/// <summary>
/// Drives the REAL fake worker (tests/Fixtures/fake_chatterbox_worker.py, stdlib-only)
/// over real pipes — no mocked Process. Skips gracefully when python3 is absent.
/// </summary>
[Trait("Category", "Integration")]
public class ChatterboxSidecarTests
{
    private static readonly string FakeWorkerPath =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "fake_chatterbox_worker.py");

    private static bool Python3Available =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator)
            .Any(dir => !string.IsNullOrWhiteSpace(dir) && File.Exists(Path.Combine(dir, "python3")));

    [Fact]
    public async Task StartAsync_SpawnsWorker_AndSecondStartIsNoOp()
    {
        if (!Python3Available)
        {
            return;
        }

        await using var sidecar = CreateSidecar();

        await sidecar.StartAsync(null, CancellationToken.None);
        sidecar.IsRunning.Should().BeTrue();

        await sidecar.StartAsync(null, CancellationToken.None);
        sidecar.IsRunning.Should().BeTrue();
    }

    [Fact]
    public async Task StartAsync_MissingWorkerScript_ThrowsWithPath()
    {
        await using var sidecar = CreateSidecar(workerPath: Path.Combine("nope", "missing_worker.py"), requireFixture: false);

        var act = () => sidecar.StartAsync(null, CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*missing_worker.py*");
    }

    [Fact]
    public async Task SpeakAsync_HappyPath_WritesWavAndReportsLoadProgress()
    {
        if (!Python3Available)
        {
            return;
        }

        await using var sidecar = CreateSidecar();
        var outPath = TempWavPath();
        var progressLines = new List<string>();

        try
        {
            await sidecar.StartAsync(null, CancellationToken.None);
            var result = await sidecar.SpeakAsync(
                new ChatterboxSpeakRequest("c1", "hello world", null, 0.5f, 0.5f, outPath),
                new Progress<string>(progressLines.Add),
                CancellationToken.None);

            result.Ok.Should().BeTrue();
            result.OutPath.Should().Be(outPath);
            result.AudioSeconds.Should().BeGreaterThan(0);
            File.Exists(outPath).Should().BeTrue();
            new FileInfo(outPath).Length.Should().BeGreaterThan(44, "a real RIFF wav has a header plus frames");

            // Progress<T> posts to the thread pool; give the load line a moment to land.
            await WaitUntilAsync(() => progressLines.Count > 0);
            progressLines.Should().Contain(l => l.StartsWith("Local narration: ") && l.Contains("Loading fake model"));
        }
        finally
        {
            File.Delete(outPath);
        }
    }

    [Fact]
    public async Task SpeakAsync_TwoSequentialSpeaks_ReuseOneWorker()
    {
        if (!Python3Available)
        {
            return;
        }

        await using var sidecar = CreateSidecar();
        var outPath1 = TempWavPath();
        var outPath2 = TempWavPath();

        try
        {
            await sidecar.StartAsync(null, CancellationToken.None);
            var first = await sidecar.SpeakAsync(new ChatterboxSpeakRequest("c1", "one", null, 0.5f, 0.5f, outPath1), null, CancellationToken.None);
            var second = await sidecar.SpeakAsync(new ChatterboxSpeakRequest("c2", "two", null, 0.5f, 0.5f, outPath2), null, CancellationToken.None);

            first.Ok.Should().BeTrue();
            second.Ok.Should().BeTrue();
            sidecar.IsRunning.Should().BeTrue("the worker process must stay warm across requests");
        }
        finally
        {
            File.Delete(outPath1);
            File.Delete(outPath2);
        }
    }

    [Fact]
    public async Task SpeakAsync_FakeFailMode_ReturnsNotOkAndKeepsServing()
    {
        if (!Python3Available)
        {
            return;
        }

        await using var sidecar = CreateSidecar();
        sidecar.EnvironmentOverrides["FAKE_CB_FAIL"] = "1";
        var outPath = TempWavPath();

        await sidecar.StartAsync(null, CancellationToken.None);
        var result = await sidecar.SpeakAsync(new ChatterboxSpeakRequest("c1", "boom", null, 0.5f, 0.5f, outPath), null, CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Error.Should().Contain("synthetic failure");
        sidecar.IsRunning.Should().BeTrue("a per-request failure must not tear the worker down");
    }

    [Fact]
    public async Task SpeakAsync_WorkerDiesMidRequest_FailsWithExitCode_AndIsRestartable()
    {
        if (!Python3Available)
        {
            return;
        }

        await using var sidecar = CreateSidecar();
        sidecar.EnvironmentOverrides["FAKE_CB_DIE"] = "1";
        var outPath = TempWavPath();

        await sidecar.StartAsync(null, CancellationToken.None);
        var act = () => sidecar.SpeakAsync(new ChatterboxSpeakRequest("c1", "die", null, 0.5f, 0.5f, outPath), null, CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*exited*1*");
        sidecar.IsRunning.Should().BeFalse();

        // Restartable: a fresh StartAsync spawns a new worker (DIE only fires on speak).
        await sidecar.StartAsync(null, CancellationToken.None);
        sidecar.IsRunning.Should().BeTrue();
    }

    [Fact]
    public async Task SpeakAsync_SlowWorkerPastTimeout_ThrowsTimeoutAndKillsWorker()
    {
        if (!Python3Available)
        {
            return;
        }

        await using var sidecar = CreateSidecar(speakTimeoutSeconds: 1);
        sidecar.EnvironmentOverrides["FAKE_CB_SLOW_MS"] = "3000";
        var outPath = TempWavPath();

        await sidecar.StartAsync(null, CancellationToken.None);
        var act = () => sidecar.SpeakAsync(new ChatterboxSpeakRequest("c1", "slow", null, 0.5f, 0.5f, outPath), null, CancellationToken.None);

        (await act.Should().ThrowAsync<TimeoutException>())
            .WithMessage("*speak timeout*");
        sidecar.IsRunning.Should().BeFalse("the timed-out worker must be killed");
    }

    [Fact]
    public async Task SpeakAsync_CancelledMidRequest_ThrowsOce_AndIsRestartable()
    {
        if (!Python3Available)
        {
            return;
        }

        await using var sidecar = CreateSidecar();
        sidecar.EnvironmentOverrides["FAKE_CB_SLOW_MS"] = "3000";
        var outPath = TempWavPath();

        await sidecar.StartAsync(null, CancellationToken.None);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var act = () => sidecar.SpeakAsync(new ChatterboxSpeakRequest("c1", "slow", null, 0.5f, 0.5f, outPath), null, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        sidecar.IsRunning.Should().BeFalse("cancellation kills the worker — generate() can't be interrupted politely");

        await sidecar.StartAsync(null, CancellationToken.None);
        sidecar.IsRunning.Should().BeTrue();
    }

    [Fact]
    public async Task StopAsync_ShutsWorkerDown()
    {
        if (!Python3Available)
        {
            return;
        }

        await using var sidecar = CreateSidecar();
        await sidecar.StartAsync(null, CancellationToken.None);
        sidecar.IsRunning.Should().BeTrue();

        await sidecar.StopAsync();

        sidecar.IsRunning.Should().BeFalse();
    }

    private static ChatterboxSidecar CreateSidecar(int speakTimeoutSeconds = 30, string? workerPath = null, bool requireFixture = true)
    {
        if (requireFixture)
        {
            File.Exists(FakeWorkerPath).Should().BeTrue($"the fake worker fixture must be copied to test output at {FakeWorkerPath}");
        }

        var config = new ChatterboxConfiguration
        {
            UvPath = "python3",
            UvArgs = string.Empty,
            WorkerRelativePath = workerPath ?? FakeWorkerPath,
            StartTimeoutSeconds = 30,
            SpeakTimeoutSecondsPerChunk = speakTimeoutSeconds,
            LoadTimeoutSeconds = 60,
        };

        return new ChatterboxSidecar(Options.Create(config), NullLogger<ChatterboxSidecar>.Instance);
    }

    private static string TempWavPath() =>
        Path.Combine(Path.GetTempPath(), $"wirecopy-cbx-test-{Guid.NewGuid():N}.wav");

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(20);
        }
    }
}
