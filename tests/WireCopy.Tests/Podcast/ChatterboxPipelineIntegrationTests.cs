// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using WireCopy.Application.DTOs;
using WireCopy.Application.Interfaces;
using WireCopy.Application.Interfaces.Podcast;
using WireCopy.Infrastructure.Configuration;
using WireCopy.Infrastructure.Podcast;
using WireCopy.Infrastructure.Podcast.Cache;
using WireCopy.Infrastructure.Podcast.Chatterbox;
using Xunit;

namespace WireCopy.Tests.Podcast;

/// <summary>
/// workspace-2xej.11 — the local-narration feature exercised the way the user
/// uses it, with REAL wiring: a real ChatterboxSidecar process (python3 + the
/// stdlib fake worker), the real ChatterboxTtsService, the real TtsEngineRouter,
/// and the real FileSystemTtsAudioCache keyed by the router. Only the torch model
/// is faked. Plus an env-gated real-model gate (CHATTERBOX_E2E=1) for the dev box.
/// </summary>
[Trait("Category", "Integration")]
public class ChatterboxPipelineIntegrationTests
{
    private static readonly string FakeWorkerPath =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "fake_chatterbox_worker.py");

    private static bool Python3AndFfmpeg =>
        PathHas("python3") && (PathHas("ffmpeg") || PathHas("ffmpeg.exe"));

    [Fact]
    public async Task FullPipeline_FakeWorker_GeneratesMultiChunkAacAndCachesUnderChatterboxKey()
    {
        if (!Python3AndFfmpeg)
        {
            return;
        }

        File.Exists(FakeWorkerPath).Should().BeTrue("the fake worker fixture must be copied to test output");

        var settings = new InMemorySettingsStore();
        settings.Set(SettingsCommandHandlerKeys.TtsEngine, "chatterbox");

        var config = Options.Create(new ChatterboxConfiguration
        {
            UvPath = "python3",
            UvArgs = string.Empty,
            WorkerRelativePath = FakeWorkerPath,
            MaxChunkSize = 300,
        });

        await using var sidecar = new ChatterboxSidecar(config, NullLogger<ChatterboxSidecar>.Instance);
        var chatterbox = new ChatterboxTtsService(sidecar, config, NullLogger<ChatterboxTtsService>.Instance, settings);
        var openAi = new OpenAiTtsService(Options.Create(new OpenAiTtsConfiguration()), NullLogger<OpenAiTtsService>.Instance, settings);
        var router = new TtsEngineRouter(openAi, chatterbox, settings, NullLogger<TtsEngineRouter>.Instance);

        var tempDir = Path.Combine(Path.GetTempPath(), $"cbx-pipeline-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var cacheConfig = Options.Create(new TtsAudioCacheConfiguration { BasePath = tempDir });
            var cache = new FileSystemTtsAudioCache(cacheConfig, router, router, NullLogger<FileSystemTtsAudioCache>.Instance);

            // ~800 chars → 3 chunks at MaxChunkSize 300.
            var text = string.Join(" ", Enumerable.Repeat("The quick brown fox jumps over the lazy dog.", 18))[..800];
            var reports = new List<TtsProgress>();

            var result = await router.GenerateAudioAsync(text, "Pipeline", new SyncProgress<TtsProgress>(reports.Add));

            result.Success.Should().BeTrue(result.ErrorMessage);
            result.ChunksCompleted.Should().Be(3);
            result.AudioData.Should().NotBeNull();
            result.AudioData!.Length.Should().BeGreaterThan(0);

            // Real AAC of the 3 concatenated 0.2 s fake chunks → > 0.4 s of audio.
            var probePath = Path.Combine(tempDir, "probe.m4a");
            await File.WriteAllBytesAsync(probePath, result.AudioData);
            var probed = await FFMpegCore.FFProbe.AnalyseAsync(probePath);
            probed.Duration.TotalSeconds.Should().BeGreaterThan(0.4);

            reports.Count(r => r.Message.Contains("complete")).Should().Be(3, "one completion report per chunk");

            // Cache round-trips under the chatterbox key; an openai-flavoured lookup misses.
            await cache.PutAsync(text, "https://example.com", "Pipeline", result.AudioData);
            (await cache.TryGetAsync(text, "https://example.com")).Should().NotBeNull("chatterbox engine → chatterbox key hits");

            settings.Set(SettingsCommandHandlerKeys.TtsEngine, "openai");
            (await cache.TryGetAsync(text, "https://example.com"))
                .Should().BeNull("flipping to openai re-partitions the cache — chatterbox audio must not be served");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    /// <summary>
    /// Real-model gate — dev machine only. Set CHATTERBOX_E2E=1 to run it; it
    /// launches uv + the real chatterbox-tts package, downloads weights on first
    /// run (can take minutes), and generates a real clip. Optional voice sample
    /// via CHATTERBOX_E2E_SAMPLE=&lt;abs path&gt;.
    /// </summary>
    [Fact]
    public async Task RealModel_Chatterbox_ProducesAudio()
    {
        if (Environment.GetEnvironmentVariable("CHATTERBOX_E2E") != "1")
        {
            return;
        }

        var settings = new InMemorySettingsStore();
        var samplePath = Environment.GetEnvironmentVariable("CHATTERBOX_E2E_SAMPLE");
        if (!string.IsNullOrWhiteSpace(samplePath))
        {
            settings.Set(SettingsCommandHandlerKeys.ChatterboxVoiceSample, samplePath);
        }

        // Default config = the real uv launch line (chatterbox-tts + setuptools<81).
        var config = Options.Create(new ChatterboxConfiguration());
        await using var sidecar = new ChatterboxSidecar(config, NullLogger<ChatterboxSidecar>.Instance);
        var service = new ChatterboxTtsService(sidecar, config, NullLogger<ChatterboxTtsService>.Instance, settings);

        var result = await service.GenerateAudioAsync(
            "This is a real Chatterbox end to end test of local narration.",
            "E2E",
            new SyncProgress<TtsProgress>(p => Console.Error.WriteLine($"[cbx-e2e] {p.Message}")));

        result.Success.Should().BeTrue(result.ErrorMessage);
        result.AudioData.Should().NotBeNull();

        var tempPath = Path.Combine(Path.GetTempPath(), $"cbx-e2e-{Guid.NewGuid():N}.m4a");
        try
        {
            await File.WriteAllBytesAsync(tempPath, result.AudioData!);
            var probed = await FFMpegCore.FFProbe.AnalyseAsync(tempPath);
            probed.Duration.TotalSeconds.Should().BeGreaterThan(1.0);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    private static bool PathHas(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator)
            .Any(dir => !string.IsNullOrWhiteSpace(dir) && File.Exists(Path.Combine(dir, name)));

    private static class SettingsCommandHandlerKeys
    {
        public const string TtsEngine = "TtsEngine";
        public const string ChatterboxVoiceSample = "ChatterboxVoiceSample";
    }

    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    private sealed class InMemorySettingsStore : IUserSettingsStore
    {
        private readonly Dictionary<string, string> _values = [];

        public string? Get(string key) => _values.TryGetValue(key, out var value) ? value : null;

        public void Set(string key, string value, bool encrypt = false) => _values[key] = value;

        public void Remove(string key) => _values.Remove(key);
    }
}
