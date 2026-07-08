// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using WireCopy.Application.DTOs;
using WireCopy.Application.Interfaces;
using WireCopy.Infrastructure.Configuration;
using WireCopy.Infrastructure.Podcast.Chatterbox;
using Xunit;

namespace WireCopy.Tests.Podcast;

[Trait("Category", "Unit")]
public class ChatterboxTtsServiceTests
{
    private static readonly string FakeWorkerPath =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "fake_chatterbox_worker.py");

    private static bool FfmpegAvailable =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator)
            .Any(dir => !string.IsNullOrWhiteSpace(dir)
                && (File.Exists(Path.Combine(dir, "ffmpeg")) || File.Exists(Path.Combine(dir, "ffmpeg.exe"))));

    [Fact]
    public void EstimateCost_900Chars_ThreeChunksAtZeroDollars()
    {
        var (service, _, _) = CreateService();

        var estimate = service.EstimateCost(new string('a', 900));

        estimate.CharacterCount.Should().Be(900);
        estimate.ChunkCount.Should().Be(3, "900 chars hard-split at MaxChunkSize 300");
        estimate.EstimatedCostUsd.Should().Be(0m);
        estimate.EstimatedDurationMinutes.Should().BeApproximately(900 / 750.0, 0.001);
    }

    [Fact]
    public async Task GenerateAudioAsync_UvMissing_FailsWithoutTouchingSidecar()
    {
        var (service, sidecar, _) = CreateService(uvPath: "definitely-not-on-path-xyz-000");

        var result = await service.GenerateAudioAsync("some text", "title");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().StartWith("Local narration: ").And.Contain("uv is not installed");
        sidecar.StartCalls.Should().Be(0);
        sidecar.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task GenerateAudioAsync_HappyPathTwoChunks_SpeaksEachChunkAndReturnsAac()
    {
        if (!FfmpegAvailable)
        {
            return;
        }

        var (service, sidecar, _) = CreateService();
        var text = new string('a', 600);

        var result = await service.GenerateAudioAsync(text, "title");

        result.Success.Should().BeTrue(result.ErrorMessage);
        result.AudioData.Should().NotBeNull();
        result.AudioData!.Length.Should().BeGreaterThan(0);
        result.ChunksCompleted.Should().Be(2);
        result.CharactersProcessed.Should().Be(600);
        sidecar.Requests.Select(r => r.Id).Should().Equal("chunk-0", "chunk-1");
        sidecar.Requests.Select(r => r.Text).Should().Equal(text[..300], text[300..]);
    }

    [Fact]
    public async Task GenerateAudioAsync_ChunkFailsOnce_RestartsWorkerAndRetriesToSuccess()
    {
        if (!FfmpegAvailable)
        {
            return;
        }

        var (service, sidecar, _) = CreateService();
        sidecar.ScriptedErrors.Enqueue(null);            // chunk-0 succeeds
        sidecar.ScriptedErrors.Enqueue("worker hiccup"); // chunk-1 first attempt fails
        var text = new string('a', 600);

        var result = await service.GenerateAudioAsync(text, "title");

        result.Success.Should().BeTrue(result.ErrorMessage);
        sidecar.StopCalls.Should().Be(1, "exactly one restart pair for the single failure");
        sidecar.StartCalls.Should().Be(2, "the initial start plus the recovery restart");
        sidecar.Requests.Select(r => r.Id).Should().Equal("chunk-0", "chunk-1", "chunk-1");
    }

    [Fact]
    public async Task GenerateAudioAsync_ChunkFailsTwice_FailsWithSentinelAndChunkInfo()
    {
        var (service, sidecar, _) = CreateService();
        sidecar.ScriptedErrors.Enqueue(null);      // chunk-0 succeeds
        sidecar.ScriptedErrors.Enqueue("boom-1");  // chunk-1 attempt 1
        sidecar.ScriptedErrors.Enqueue("boom-2");  // chunk-1 attempt 2 (post-restart)
        var text = new string('a', 600);

        var result = await service.GenerateAudioAsync(text, "title");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().StartWith("Local narration: ").And.Contain("boom-2");
        result.CharactersProcessed.Should().Be(300, "only chunk-0 completed");
        result.ChunksCompleted.Should().Be(1, "the failure hit the second chunk (index 1)");
    }

    [Theory]
    [InlineData("0.9", 0.9f)]
    [InlineData(null, 0.5f)]
    [InlineData("abc", 0.5f)]
    [InlineData("7", 2f)]
    public void GetEffectiveExaggeration_SettingsWinWithFallbackAndClamp(string? saved, float expected)
    {
        var (service, _, settings) = CreateService();
        settings.Get("ChatterboxExaggeration").Returns(saved);

        service.GetEffectiveExaggeration().Should().Be(expected);
    }

    [Fact]
    public async Task GenerateAudioAsync_ExaggerationSetting_ReachesTheSpeakRequests()
    {
        if (!FfmpegAvailable)
        {
            return;
        }

        var (service, sidecar, settings) = CreateService();
        settings.Get("ChatterboxExaggeration").Returns("0.9");

        var result = await service.GenerateAudioAsync("short text", "title");

        result.Success.Should().BeTrue(result.ErrorMessage);
        sidecar.Requests.Should().OnlyContain(r => Math.Abs(r.Exaggeration - 0.9f) < 0.0001f);
    }

    [Fact]
    public async Task GenerateAudioAsync_ExistingSample_ForwardedToRequests()
    {
        if (!FfmpegAvailable)
        {
            return;
        }

        var samplePath = Path.Combine(Path.GetTempPath(), $"wirecopy-cbx-sample-{Guid.NewGuid():N}.wav");
        await File.WriteAllBytesAsync(samplePath, TinyWav());
        try
        {
            var (service, sidecar, settings) = CreateService();
            settings.Get("ChatterboxVoiceSample").Returns(samplePath);

            var result = await service.GenerateAudioAsync("short text", "title");

            result.Success.Should().BeTrue(result.ErrorMessage);
            sidecar.Requests.Should().OnlyContain(r => r.SamplePath == samplePath);
        }
        finally
        {
            File.Delete(samplePath);
        }
    }

    [Fact]
    public async Task GenerateAudioAsync_MissingSample_FallsBackToBuiltInVoice()
    {
        if (!FfmpegAvailable)
        {
            return;
        }

        var (service, sidecar, settings) = CreateService();
        settings.Get("ChatterboxVoiceSample").Returns(Path.Combine(Path.GetTempPath(), "definitely-missing-sample.wav"));

        var result = await service.GenerateAudioAsync("short text", "title");

        result.Success.Should().BeTrue(result.ErrorMessage);
        sidecar.Requests.Should().OnlyContain(r => r.SamplePath == null);
    }

    [Fact]
    public void ResolveSamplePath_Unset_IsNull()
    {
        var (service, _, settings) = CreateService();
        settings.Get("ChatterboxVoiceSample").Returns((string?)null);

        service.ResolveSamplePath().Should().BeNull();
    }

    [Fact]
    public async Task GenerateAudioAsync_ReportsPerChunkProgress()
    {
        if (!FfmpegAvailable)
        {
            return;
        }

        var (service, _, _) = CreateService();
        var reports = new List<TtsProgress>();
        var text = new string('a', 600);

        var result = await service.GenerateAudioAsync(text, "title", new SyncProgress<TtsProgress>(reports.Add));

        result.Success.Should().BeTrue(result.ErrorMessage);
        var chunkReports = reports.Where(r => r.Message.Contains("complete")).ToList();
        chunkReports.Select(r => r.CurrentChunk).Should().Equal(1, 2);
        chunkReports.Should().OnlyContain(r => r.TotalChunks == 2 && r.TotalCharacters == 600);
        chunkReports[^1].CharactersProcessed.Should().Be(600);
    }

    [Fact]
    public async Task ValidateApiKeyAsync_ChecksInOrder_UvWorkerSample()
    {
        var (noUv, _, _) = CreateService(uvPath: "definitely-not-on-path-xyz-000");
        (await noUv.ValidateApiKeyAsync()).ErrorCode.Should().Be("uv_missing");

        var (noWorker, _, _) = CreateService(workerPath: Path.Combine("nope", "gone.py"));
        (await noWorker.ValidateApiKeyAsync()).ErrorCode.Should().Be("worker_missing");

        var (badSample, _, badSampleSettings) = CreateService();
        badSampleSettings.Get("ChatterboxVoiceSample").Returns(Path.Combine(Path.GetTempPath(), "definitely-missing-sample.wav"));
        (await badSample.ValidateApiKeyAsync()).ErrorCode.Should().Be("sample_missing");

        var (ready, _, _) = CreateService();
        (await ready.ValidateApiKeyAsync()).IsValid.Should().BeTrue();
    }

    private static (ChatterboxTtsService Service, FakeSidecar Sidecar, IUserSettingsStore Settings) CreateService(
        string uvPath = "python3",
        string? workerPath = null)
    {
        var settings = Substitute.For<IUserSettingsStore>();
        var sidecar = new FakeSidecar();
        var config = new ChatterboxConfiguration
        {
            UvPath = uvPath,
            UvArgs = string.Empty,
            WorkerRelativePath = workerPath ?? FakeWorkerPath,
        };
        var service = new ChatterboxTtsService(sidecar, Options.Create(config), NullLogger<ChatterboxTtsService>.Instance, settings);
        return (service, sidecar, settings);
    }

    private static byte[] TinyWav()
    {
        const int sampleRate = 24000;
        const short channels = 1;
        const short bitsPerSample = 16;
        const int frames = 2400; // 0.1 s
        var dataSize = frames * channels * (bitsPerSample / 8);

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8);
        w.Write(36 + dataSize);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);
        w.Write((short)1); // PCM
        w.Write(channels);
        w.Write(sampleRate);
        w.Write(sampleRate * channels * (bitsPerSample / 8));
        w.Write((short)(channels * (bitsPerSample / 8)));
        w.Write(bitsPerSample);
        w.Write("data"u8);
        w.Write(dataSize);
        for (var i = 0; i < frames; i++)
        {
            w.Write((short)(8000 * Math.Sin(2 * Math.PI * 440 * i / sampleRate)));
        }

        w.Flush();
        return ms.ToArray();
    }

    /// <summary>Synchronous IProgress — Progress&lt;T&gt; posts to the pool and races test asserts.</summary>
    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    /// <summary>
    /// Records every call; per-speak outcomes scripted via <see cref="ScriptedErrors"/>
    /// (null = success, string = ok:false error). Success writes a real tiny WAV so
    /// the ffmpeg assembly step downstream consumes genuine audio.
    /// </summary>
    private sealed class FakeSidecar : IChatterboxSidecar
    {
        public List<ChatterboxSpeakRequest> Requests { get; } = [];

        public Queue<string?> ScriptedErrors { get; } = new();

        public int StartCalls { get; private set; }

        public int StopCalls { get; private set; }

        public bool IsRunning => true;

        public Task StartAsync(IProgress<string>? setupProgress, CancellationToken ct)
        {
            StartCalls++;
            return Task.CompletedTask;
        }

        public async Task<ChatterboxSpeakResult> SpeakAsync(ChatterboxSpeakRequest request, IProgress<string>? progress, CancellationToken ct)
        {
            Requests.Add(request);
            var error = ScriptedErrors.Count > 0 ? ScriptedErrors.Dequeue() : null;
            if (error is not null)
            {
                return new ChatterboxSpeakResult(false, null, 0, error);
            }

            await File.WriteAllBytesAsync(request.OutPath, TinyWav(), ct);
            return new ChatterboxSpeakResult(true, request.OutPath, 0.1, null);
        }

        public Task StopAsync()
        {
            StopCalls++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
