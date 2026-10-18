// Licensed under the MIT License. See LICENSE in the repository root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using WireCopy.Application.DTOs.Podcast;
using WireCopy.Application.Interfaces;
using WireCopy.Infrastructure.Browser.CommandHandlers;
using WireCopy.Infrastructure.Podcast.AudioEngine;
using Xunit;

namespace WireCopy.Tests.Podcast;

/// <summary>
/// Unit tests for <see cref="AudioEngineNarrator"/> against a routing stub of the audio-engine HTTP API
/// (workspace-c3ec4.1), plus one live test that only runs when AUDIO_ENGINE_E2E_URL / _KEY are set.
/// </summary>
[Trait("Category", "Unit")]
public sealed class AudioEngineNarratorTests : IDisposable
{
    private const string Url = "http://127.0.0.1:8090";
    private const string Key = "unit-key";
    private const string JobId = "job-123";

    private const string UnauthorizedText = "audio-engine rejected the API key. Set it in Settings -> Podcast narration.";

    // 1x1 transparent PNG: enough for CoverArtFromFile's magic-byte check.
    private const string TinyPngBase64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";

    private static readonly byte[] Audio = [0x00, 0x00, 0x00, 0x1C, 0x66, 0x74, 0x79, 0x70, 0x4D, 0x34, 0x42, 0x20];

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "wirecopy-narrator-tests", Guid.NewGuid().ToString("N"));
    private readonly IUserSettingsStore _store = Substitute.For<IUserSettingsStore>();
    private readonly RoutingHandler _http = new();

    public AudioEngineNarratorTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    // Guards:   NarrateAsync submits exactly the job audio-engine needs: m4b, every chapter in order, skip-on-chapter-error, tags, list id and the cover art when the file exists.
    // Fails if: BuildJobRequest drops a chapter, changes the format, forgets onChapterError, or sends cover art for a missing file.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~AudioEngineNarratorTests.NarrateAsync_SubmitsChaptersTagsAndCoverArt"
    [Fact]
    public async Task NarrateAsync_SubmitsChaptersTagsAndCoverArt()
    {
        Configure();
        MapHappyPath();
        var cover = Path.Combine(_tempDir, "cover.png");
        await File.WriteAllBytesAsync(cover, Convert.FromBase64String(TinyPngBase64));
        var narrator = Narrator();

        var withCover = await narrator.NarrateAsync(Request(coverArtPath: cover));
        withCover.Success.Should().BeTrue(withCover.ErrorMessage);

        using var doc = JsonDocument.Parse(SubmitBodies().Single());
        var root = doc.RootElement;
        root.GetProperty("format").GetString().Should().Be("m4b");
        root.GetProperty("engine").GetString().Should().Be("openai");
        root.GetProperty("onChapterError").GetString().Should().Be("skip");
        root.GetProperty("author").GetString().Should().Be("WireCopy");
        root.GetProperty("album").GetString().Should().Be("Reading List");
        root.GetProperty("genre").GetString().Should().Be("News");
        root.GetProperty("description").GetString().Should().Be("Two stories");
        root.GetProperty("metadata").GetProperty("readingListId").GetString().Should().Be("list-1");
        root.GetProperty("coverArt").GetString().Should().StartWith("data:image/png;base64,");
        root.GetProperty("idempotencyKey").GetString().Should().StartWith("wirecopy-");
        var chapters = root.GetProperty("chapters").EnumerateArray().ToList();
        chapters.Should().HaveCount(2);
        chapters[0].GetProperty("title").GetString().Should().Be("One");
        chapters[0].GetProperty("text").GetString().Should().Be("First chapter text.");
        chapters[1].GetProperty("title").GetString().Should().Be("Two");
        chapters[1].GetProperty("text").GetString().Should().Be("Second chapter text.");
        _http.Requests.Single(r => r.Path == "v1/jobs").Bearer.Should().Be(Key);

        // A cover path that does not exist is left out, not sent as garbage.
        _http.Requests.Clear();
        var withoutCover = await narrator.NarrateAsync(Request(coverArtPath: Path.Combine(_tempDir, "missing.png")));
        withoutCover.Success.Should().BeTrue(withoutCover.ErrorMessage);
        using var doc2 = JsonDocument.Parse(SubmitBodies().Single());
        var hasCover = doc2.RootElement.TryGetProperty("coverArt", out var coverArt) && coverArt.ValueKind != JsonValueKind.Null;
        hasCover.Should().BeFalse();
    }

    // Guards:   The idempotency key is stable for the same input and changes when the text or the voice changes, so audio-engine replays only true duplicates.
    // Fails if: The key ignores chapter text or the voice setting, or includes something non-deterministic.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~AudioEngineNarratorTests.NarrateAsync_IdempotencyKey_TracksTextAndVoice"
    [Fact]
    public async Task NarrateAsync_IdempotencyKey_TracksTextAndVoice()
    {
        Configure();
        _store.Get(SettingsCommandHandler.KeyOpenAiTtsVoice).Returns("nova");
        MapHappyPath();
        var narrator = Narrator();

        await narrator.NarrateAsync(Request());
        await narrator.NarrateAsync(Request());
        await narrator.NarrateAsync(Request(chapterTwoText: "Second chapter text, edited."));
        _store.Get(SettingsCommandHandler.KeyOpenAiTtsVoice).Returns("alloy");
        await narrator.NarrateAsync(Request());

        var keys = SubmitBodies().Select(IdempotencyKeyOf).ToList();
        keys.Should().HaveCount(4);
        keys[1].Should().Be(keys[0]);
        keys[2].Should().NotBe(keys[0]);
        keys[3].Should().NotBe(keys[0]);
        keys[3].Should().NotBe(keys[2]);
    }

    // Guards:   Progress events from audio-engine reach the caller with phase, chapter and fraction, and a succeeded job yields the downloaded file plus its duration, cost and job id.
    // Fails if: The SSE mapping drops chapter info, the phase is not parsed, or the result fields are not copied from the job.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~AudioEngineNarratorTests.NarrateAsync_ReportsProgressAndDownloadsResult"
    [Fact]
    public async Task NarrateAsync_ReportsProgressAndDownloadsResult()
    {
        Configure();
        MapHappyPath(
            sse: Sse(
                Event("job.progress", "running", "synthesizing", 0.3, chunksDone: 1, chunksTotal: 4, message: "Chapter 1 of 2", chapterIndex: 0, chapterTitle: "One", chaptersTotal: 2),
                Event("job.progress", "running", "assembling", 0.9, chunksDone: 4, chunksTotal: 4),
                Event("job.succeeded", "succeeded", "done", 1.0, chunksDone: 4, chunksTotal: 4)));
        var sink = new ListProgress();
        var request = Request();

        var result = await Narrator().NarrateAsync(request, sink);

        sink.Items.Should().Contain(p =>
            p.Phase == NarrationPhase.Synthesizing && p.Fraction == 0.3 && p.ChapterIndex == 0 && p.ChapterTitle == "One" && p.ChaptersTotal == 2 && p.ChunksDone == 1 && p.ChunksTotal == 4);
        sink.Items.Should().Contain(p => p.Phase == NarrationPhase.Assembling && p.Fraction == 0.9);

        result.Success.Should().BeTrue(result.ErrorMessage);
        result.ErrorKind.Should().Be(NarrationErrorKind.None);
        result.JobId.Should().Be(JobId);
        result.Bytes.Should().Be(Audio.Length);
        result.Duration.Should().Be(TimeSpan.FromSeconds(12.5));
        result.CostUsd.Should().Be(0.42m);
        result.OutputPath.Should().Be(request.OutputPath);
        (await File.ReadAllBytesAsync(request.OutputPath)).Should().Equal(Audio);
        _http.Requests.Select(r => $"{r.Method} {r.Path}").Should().ContainInOrder(
            "POST v1/jobs", $"GET v1/jobs/{JobId}/events", $"GET v1/jobs/{JobId}", $"GET v1/jobs/{JobId}/audio");
    }

    // Guards:   A chapter audio-engine skipped (onChapterError=skip) is reported to the caller while the narration still counts as a success.
    // Fails if: skippedChapters is not mapped from the job, or a skipped chapter turns the result into a failure.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~AudioEngineNarratorTests.NarrateAsync_SkippedChapters_AreReported"
    [Fact]
    public async Task NarrateAsync_SkippedChapters_AreReported()
    {
        Configure();
        MapHappyPath(jobJson: JobJson("succeeded", skippedChapters: "[{\"index\":1,\"title\":\"Two\",\"error\":\"boom\"}]"));

        var result = await Narrator().NarrateAsync(Request());

        result.Success.Should().BeTrue(result.ErrorMessage);
        result.SkippedChapters.Should().ContainSingle()
            .Which.Should().Be(new NarrationSkippedChapter(1, "Two", "boom"));
    }

    // Guards:   A job that fails inside audio-engine surfaces as ErrorKind Failed with audio-engine's own error text.
    // Fails if: A failed job is treated as success, or the error text is lost or not prefixed with "audio-engine:".
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~AudioEngineNarratorTests.NarrateAsync_FailedJob_ReportsFailed"
    [Fact]
    public async Task NarrateAsync_FailedJob_ReportsFailed()
    {
        Configure();
        MapHappyPath(
            sse: Sse(Event("job.failed", "failed", "synthesizing", 0.4, chunksDone: 1, chunksTotal: 4)),
            jobJson: JobJson("failed", error: "ffmpeg died"));

        var result = await Narrator().NarrateAsync(Request());

        result.Success.Should().BeFalse();
        result.ErrorKind.Should().Be(NarrationErrorKind.Failed);
        result.ErrorMessage.Should().Be("audio-engine: ffmpeg died");
        result.JobId.Should().Be(JobId);
        _http.Requests.Should().NotContain(r => r.Path.EndsWith("/audio", StringComparison.Ordinal));
    }

    // Guards:   A 402 from audio-engine becomes BudgetExceeded with the estimate and limit in dollars, and nothing is downloaded.
    // Fails if: The 402 body is not parsed, the message format changes, or the narrator continues past the refused submit.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~AudioEngineNarratorTests.NarrateAsync_Submit402_ReportsBudgetExceeded"
    [Fact]
    public async Task NarrateAsync_Submit402_ReportsBudgetExceeded()
    {
        Configure();
        _http.Map(HttpMethod.Post, "v1/jobs", HttpStatusCode.PaymentRequired, "{\"estimateUsd\":7.5,\"limitUsd\":5.0,\"reason\":\"max_job_cost\"}");

        var result = await Narrator().NarrateAsync(Request());

        result.Success.Should().BeFalse();
        result.ErrorKind.Should().Be(NarrationErrorKind.BudgetExceeded);
        result.ErrorMessage.Should().Be("audio-engine: narration would cost $7.50, over the $5.00 limit.");
        _http.Requests.Should().OnlyContain(r => r.Path == "v1/jobs");
    }

    // Guards:   A 401 from audio-engine becomes Unauthorized with the message that points the user at Settings.
    // Fails if: 401 is classified as something else or the message text drifts.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~AudioEngineNarratorTests.NarrateAsync_Submit401_ReportsUnauthorized"
    [Fact]
    public async Task NarrateAsync_Submit401_ReportsUnauthorized()
    {
        Configure();
        _http.Map(HttpMethod.Post, "v1/jobs", HttpStatusCode.Unauthorized, "{\"title\":\"Unauthorized\",\"status\":401}");

        var result = await Narrator().NarrateAsync(Request());

        result.Success.Should().BeFalse();
        result.ErrorKind.Should().Be(NarrationErrorKind.Unauthorized);
        result.ErrorMessage.Should().Be(UnauthorizedText);
    }

    // Guards:   When nothing is listening at the configured address the user is told how to start audio-engine, with the address they configured.
    // Fails if: A connection failure is classified as Failed/Transient, or the message loses the URL.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~AudioEngineNarratorTests.NarrateAsync_ConnectionFails_ReportsUnreachable"
    [Fact]
    public async Task NarrateAsync_ConnectionFails_ReportsUnreachable()
    {
        Configure();
        _http.Throws = new HttpRequestException("Connection refused");

        var result = await Narrator().NarrateAsync(Request());

        result.Success.Should().BeFalse();
        result.ErrorKind.Should().Be(NarrationErrorKind.Unreachable);
        result.ErrorMessage.Should().Be(
            $"audio-engine isn't running at {Url}. Start it (audio-engine README: Run natively) or change its address in Settings.");
    }

    // Guards:   Without an API key the narrator reports NotConfigured and never touches the network; IsConfigured mirrors that.
    // Fails if: A request is sent without a key, or IsConfigured is true with a missing key or false with url+key set.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~AudioEngineNarratorTests.NarrateAsync_NoApiKey_ReportsNotConfiguredWithoutRequests"
    [Fact]
    public async Task NarrateAsync_NoApiKey_ReportsNotConfiguredWithoutRequests()
    {
        Configure(key: null);
        MapHappyPath();
        var narrator = Narrator();

        narrator.IsConfigured.Should().BeFalse();
        var result = await narrator.NarrateAsync(Request());

        result.Success.Should().BeFalse();
        result.ErrorKind.Should().Be(NarrationErrorKind.NotConfigured);
        result.ErrorMessage.Should().Be("audio-engine has no API key set. Set it in Settings -> Podcast narration.");
        _http.Requests.Should().BeEmpty();

        Configure(url: Url, key: Key);
        narrator.IsConfigured.Should().BeTrue();
    }

    // Guards:   CheckAsync tells the user their OpenAI engine has no key inside audio-engine, and where to add it.
    // Fails if: An unconfigured engine is reported as ready, or the message loses the audio-engine URL.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~AudioEngineNarratorTests.CheckAsync_OpenAiNotConfigured_ReportsEngineNotConfigured"
    [Fact]
    public async Task CheckAsync_OpenAiNotConfigured_ReportsEngineNotConfigured()
    {
        Configure();
        MapCheck();

        var status = await Narrator().CheckAsync();

        status.Reachable.Should().BeTrue();
        status.Authorized.Should().BeTrue();
        status.Ready.Should().BeFalse();
        status.EngineName.Should().Be("openai");
        status.EngineConfigured.Should().BeFalse();
        status.ErrorKind.Should().Be(NarrationErrorKind.EngineNotConfigured);
        status.Message.Should().Be($"audio-engine has no OpenAI key. Add it in audio-engine's settings ({Url}).");
        status.Engines.Select(e => e.Name).Should().Equal("openai", "chatterbox");
        _http.Requests.Single(r => r.Path == "readyz").Bearer.Should().BeNull();
        _http.Requests.Single(r => r.Path == "v1/engines").Bearer.Should().Be(Key);
    }

    // Guards:   CheckAsync reports Ready when the selected engine (chatterbox) is configured inside audio-engine.
    // Fails if: The TtsEngine setting is ignored, or a configured engine is reported as not ready.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~AudioEngineNarratorTests.CheckAsync_ChatterboxConfigured_IsReady"
    [Fact]
    public async Task CheckAsync_ChatterboxConfigured_IsReady()
    {
        Configure(engine: "chatterbox");
        MapCheck();

        var status = await Narrator().CheckAsync();

        status.Ready.Should().BeTrue(status.Message);
        status.EngineName.Should().Be("chatterbox");
        status.EngineConfigured.Should().BeTrue();
        status.ErrorKind.Should().Be(NarrationErrorKind.None);
        status.Url.Should().Be(Url);
    }

    // Guards:   CheckAsync reports Unreachable when the readiness probe cannot connect.
    // Fails if: A connection failure on /readyz is reported as anything but Unreachable, or the check throws.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~AudioEngineNarratorTests.CheckAsync_ReadyzThrows_ReportsUnreachable"
    [Fact]
    public async Task CheckAsync_ReadyzThrows_ReportsUnreachable()
    {
        Configure();
        _http.Throws = new HttpRequestException("Connection refused");

        var status = await Narrator().CheckAsync();

        status.Reachable.Should().BeFalse();
        status.Ready.Should().BeFalse();
        status.ErrorKind.Should().Be(NarrationErrorKind.Unreachable);
        status.Message.Should().Be(
            $"audio-engine isn't running at {Url}. Start it (audio-engine README: Run natively) or change its address in Settings.");
    }

    // Guards:   CheckAsync reports Unauthorized when audio-engine rejects the key on /v1/engines, while still marking it reachable.
    // Fails if: 401 on the engines probe is classified as Unreachable or EngineNotConfigured.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~AudioEngineNarratorTests.CheckAsync_Engines401_ReportsUnauthorized"
    [Fact]
    public async Task CheckAsync_Engines401_ReportsUnauthorized()
    {
        Configure();
        _http.Map(HttpMethod.Get, "readyz", HttpStatusCode.OK, "{\"status\":\"ready\"}");
        _http.Map(HttpMethod.Get, "v1/engines", HttpStatusCode.Unauthorized, "{\"title\":\"Unauthorized\",\"status\":401}");

        var status = await Narrator().CheckAsync();

        status.Reachable.Should().BeTrue();
        status.Authorized.Should().BeFalse();
        status.Ready.Should().BeFalse();
        status.ErrorKind.Should().Be(NarrationErrorKind.Unauthorized);
        status.Message.Should().Be(UnauthorizedText);
    }

    // Guards:   EstimateAsync maps audio-engine's estimate (cost, duration, budget verdict, per-chapter cache hits) and flags chatterbox as local.
    // Fails if: Any estimate field is dropped or mis-typed, or EngineIsLocal is false for chatterbox.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~AudioEngineNarratorTests.EstimateAsync_MapsEstimateAndBudget"
    [Fact]
    public async Task EstimateAsync_MapsEstimateAndBudget()
    {
        Configure(engine: "chatterbox");
        _http.Map(HttpMethod.Post, "v1/jobs/estimate", HttpStatusCode.OK,
            "{\"engine\":\"chatterbox\",\"format\":\"m4b\",\"engineConfigured\":true,\"estimateUsd\":1.25,\"fullEstimateUsd\":2.5," +
            "\"chunksTotal\":4,\"chunksCached\":2,\"characters\":800,\"estimatedDurationSeconds\":64.0," +
            "\"chapters\":[{\"index\":0,\"title\":\"One\",\"chunksTotal\":2,\"chunksCached\":2,\"characters\":400,\"estimateUsd\":0,\"estimatedDurationSeconds\":32.0}," +
            "{\"index\":1,\"title\":\"Two\",\"chunksTotal\":2,\"chunksCached\":0,\"characters\":400,\"estimateUsd\":1.25,\"estimatedDurationSeconds\":32.0}]," +
            "\"budget\":{\"allowed\":false,\"reason\":\"max_job_cost\",\"limitUsd\":5}}");

        var estimate = await Narrator().EstimateAsync(Request());

        estimate.Success.Should().BeTrue(estimate.ErrorMessage);
        estimate.EstimateUsd.Should().Be(1.25m);
        estimate.FullEstimateUsd.Should().Be(2.5m);
        estimate.EstimatedDurationSeconds.Should().Be(64.0);
        estimate.BudgetAllowed.Should().BeFalse();
        estimate.BudgetReason.Should().Be("max_job_cost");
        estimate.BudgetLimitUsd.Should().Be(5m);
        estimate.EngineIsLocal.Should().BeTrue();
        estimate.Chapters.Should().Equal(
            new NarrationChapterEstimate(0, "One", 2, 2, 0m),
            new NarrationChapterEstimate(1, "Two", 2, 0, 1.25m));

        using var doc = JsonDocument.Parse(_http.Requests.Single(r => r.Path == "v1/jobs/estimate").Body!);
        doc.RootElement.GetProperty("engine").GetString().Should().Be("chatterbox");
        doc.RootElement.TryGetProperty("voice", out _).Should().BeFalse("chatterbox has no OpenAI voice");
    }

    // Guards:   audio-engine's 400 "Engine not configured" problem on submit becomes EngineNotConfigured, not a generic rejection.
    // Fails if: The title/detail text is not recognised and the 400 falls through to Rejected.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~AudioEngineNarratorTests.NarrateAsync_Submit400EngineNotConfigured_ReportsEngineNotConfigured"
    [Fact]
    public async Task NarrateAsync_Submit400EngineNotConfigured_ReportsEngineNotConfigured()
    {
        Configure();
        _http.Map(HttpMethod.Post, "v1/jobs", HttpStatusCode.BadRequest,
            "{\"title\":\"Engine not configured\",\"status\":400,\"detail\":\"The 'openai' engine has no API key configured. Add one in audio-engine's settings.\"}");

        var result = await Narrator().NarrateAsync(Request());

        result.Success.Should().BeFalse();
        result.ErrorKind.Should().Be(NarrationErrorKind.EngineNotConfigured);
        result.ErrorMessage.Should().Be($"audio-engine has no OpenAI key. Add it in audio-engine's settings ({Url}).");
    }

    // Guards:   NarrateTestAsync asks /v1/audio/speech for the selected engine and the given text and writes the returned audio to the path.
    // Fails if: The request body names the wrong model/input, or the bytes are not written to outputPath.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~AudioEngineNarratorTests.NarrateTestAsync_PostsSpeechAndWritesFile"
    [Fact]
    public async Task NarrateTestAsync_PostsSpeechAndWritesFile()
    {
        Configure();
        _http.Map(HttpMethod.Post, "v1/audio/speech", _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Audio) });
        var output = Path.Combine(_tempDir, "sample", "test.mp3");

        var result = await Narrator().NarrateTestAsync("Hello from WireCopy.", output);

        result.Success.Should().BeTrue(result.ErrorMessage);
        result.OutputPath.Should().Be(output);
        result.Bytes.Should().Be(Audio.Length);
        (await File.ReadAllBytesAsync(output)).Should().Equal(Audio);
        using var doc = JsonDocument.Parse(_http.Requests.Single(r => r.Path == "v1/audio/speech").Body!);
        doc.RootElement.GetProperty("model").GetString().Should().Be("openai");
        doc.RootElement.GetProperty("input").GetString().Should().Be("Hello from WireCopy.");
    }

    // Guards:   Against a real audio-engine (AUDIO_ENGINE_E2E_URL + _KEY), two chatterbox chapters come back as a non-empty m4b on disk.
    // Fails if: The real submit/wait/download path breaks; passes trivially (returns early) when the env vars are not set.
    // Run:      AUDIO_ENGINE_E2E_URL=http://127.0.0.1:8090 AUDIO_ENGINE_E2E_KEY=trial-key ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~AudioEngineNarratorTests.AudioEngineNarrator_Live_NarratesTwoChapters"
    [Fact]
    public async Task AudioEngineNarrator_Live_NarratesTwoChapters()
    {
        var url = Environment.GetEnvironmentVariable("AUDIO_ENGINE_E2E_URL");
        var key = Environment.GetEnvironmentVariable("AUDIO_ENGINE_E2E_KEY");
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient());
        Configure(url: url, key: key, engine: "chatterbox");
        var narrator = new AudioEngineNarrator(
            _store, Options.Create(new AudioEngineConfiguration()), factory, NullLogger<AudioEngineNarrator>.Instance, _ => null);
        var request = new NarrationRequest
        {
            Title = "Live two-chapter check",
            Chapters =
            [
                new NarrationChapter("One", "The first chapter is one short sentence."),
                new NarrationChapter("Two", "The second chapter is another short sentence."),
            ],
            // AUDIO_ENGINE_E2E_OUT keeps the m4b outside the temp dir so a person can open it after the run.
            OutputPath = Environment.GetEnvironmentVariable("AUDIO_ENGINE_E2E_OUT") ?? Path.Combine(_tempDir, "live.m4b"),
            ListId = "e2e-two-chapters",
        };
        var sink = new ListProgress();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));

        var result = await narrator.NarrateAsync(request, sink, cts.Token);

        result.Success.Should().BeTrue($"narration failed: {result.ErrorKind} {result.ErrorMessage}");
        File.Exists(request.OutputPath).Should().BeTrue();
        result.Bytes.Should().BePositive();
        new FileInfo(request.OutputPath).Length.Should().Be(result.Bytes);
        result.JobId.Should().NotBeNullOrEmpty();
    }

    private static string IdempotencyKeyOf(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("idempotencyKey").GetString()!;
    }

    private static string Sse(params string[] eventJsons) =>
        string.Concat(eventJsons.Select(j => "data: " + j + "\n\n"));

    private static string Event(
        string type,
        string status,
        string phase,
        double progress,
        int chunksDone = 0,
        int chunksTotal = 0,
        string? message = null,
        int? chapterIndex = null,
        string? chapterTitle = null,
        int? chaptersTotal = null) =>
        JsonSerializer.Serialize(new
        {
            jobId = JobId,
            type,
            status,
            phase,
            progress,
            chunksDone,
            chunksTotal,
            message,
            chapterIndex,
            chapterTitle,
            chaptersTotal,
        });

    private static string JobJson(string status, string? error = null, string? skippedChapters = null) =>
        "{\"id\":\"" + JobId + "\",\"status\":\"" + status + "\",\"phase\":\"done\",\"progress\":1,\"chunksDone\":4,\"chunksTotal\":4," +
        "\"error\":" + (error is null ? "null" : "\"" + error + "\"") + ",\"estCostUsd\":0.42,\"durationSeconds\":12.5,\"bytes\":12," +
        "\"skippedChapters\":" + (skippedChapters ?? "null") + ",\"metadata\":{\"readingListId\":\"list-1\"}}";

    private IEnumerable<string> SubmitBodies() =>
        _http.Requests.Where(r => r.Method == HttpMethod.Post && r.Path == "v1/jobs").Select(r => r.Body!);

    private void Configure(string? url = Url, string? key = Key, string? engine = null)
    {
        _store.Get(Arg.Any<string>()).Returns((string?)null);
        _store.Get("AudioEngineUrl").Returns(url);
        _store.Get("AudioEngineApiKey").Returns(key);
        _store.Get(SettingsCommandHandler.KeyTtsEngine).Returns(engine);
    }

    private AudioEngineNarrator Narrator()
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(_http, disposeHandler: false));
        return new AudioEngineNarrator(
            _store, Options.Create(new AudioEngineConfiguration()), factory, NullLogger<AudioEngineNarrator>.Instance, _ => null);
    }

    private void MapHappyPath(string? sse = null, string? jobJson = null)
    {
        _http.Map(HttpMethod.Post, "v1/jobs", HttpStatusCode.Accepted, "{\"id\":\"" + JobId + "\",\"status\":\"pending\",\"createdAt\":\"2026-10-18T00:00:00Z\"}");
        _http.Map(HttpMethod.Get, $"v1/jobs/{JobId}/events", HttpStatusCode.OK, sse ?? Sse(Event("job.succeeded", "succeeded", "done", 1.0, 4, 4)), "text/event-stream");
        _http.Map(HttpMethod.Get, $"v1/jobs/{JobId}", HttpStatusCode.OK, jobJson ?? JobJson("succeeded"));
        _http.Map(HttpMethod.Get, $"v1/jobs/{JobId}/audio", _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Audio) });
    }

    private void MapCheck()
    {
        _http.Map(HttpMethod.Get, "readyz", HttpStatusCode.OK, "{\"status\":\"ready\"}");
        _http.Map(HttpMethod.Get, "v1/engines", HttpStatusCode.OK,
            "[{\"name\":\"openai\",\"isConfigured\":false,\"voices\":[\"alloy\",\"coral\"],\"defaultVoice\":\"coral\"}," +
            "{\"name\":\"chatterbox\",\"isConfigured\":true,\"voices\":[],\"defaultVoice\":null}]");
    }

    private NarrationRequest Request(string? coverArtPath = null, string chapterTwoText = "Second chapter text.") => new()
    {
        Title = "Morning Brief",
        Author = "WireCopy",
        Album = "Reading List",
        Genre = "News",
        Description = "Two stories",
        CoverArtPath = coverArtPath,
        Chapters =
        [
            new NarrationChapter("One", "First chapter text."),
            new NarrationChapter("Two", chapterTwoText),
        ],
        OutputPath = Path.Combine(_tempDir, "out", "list.m4b"),
        ListId = "list-1",
    };

    private sealed record RecordedRequest(HttpMethod Method, string Path, string? Body, string? Bearer);

    private sealed class ListProgress : IProgress<NarrationProgress>
    {
        public List<NarrationProgress> Items { get; } = [];

        public void Report(NarrationProgress value) => Items.Add(value);
    }

    /// <summary>Routes "METHOD path" to a response factory and records every request (with its body) for assertions.</summary>
    private sealed class RoutingHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> _routes = new(StringComparer.Ordinal);

        public List<RecordedRequest> Requests { get; } = [];

        /// <summary>When set, every send throws this instead of answering (simulates "nothing listening").</summary>
        public Exception? Throws { get; set; }

        public void Map(HttpMethod method, string path, Func<HttpRequestMessage, HttpResponseMessage> respond) =>
            _routes[$"{method} {path}"] = respond;

        public void Map(HttpMethod method, string path, HttpStatusCode status, string body, string mediaType = "application/json") =>
            Map(method, path, _ => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) });

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath.TrimStart('/');
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RecordedRequest(request.Method, path, body, request.Headers.Authorization?.Parameter));

            if (Throws is not null)
            {
                throw Throws;
            }

            if (!_routes.TryGetValue($"{request.Method} {path}", out var respond))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent($"{{\"title\":\"no stub route for {request.Method} {path}\",\"status\":404}}", Encoding.UTF8, "application/json"),
                    RequestMessage = request,
                };
            }

            var response = respond(request);
            response.RequestMessage = request;
            return response;
        }
    }
}
