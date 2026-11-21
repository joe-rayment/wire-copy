// Licensed under the MIT License. See LICENSE in the repository root.

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AudioEngine.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WireCopy.Application.DTOs.Podcast;
using WireCopy.Application.Interfaces;
using WireCopy.Application.Interfaces.Podcast;

namespace WireCopy.Infrastructure.Podcast.AudioEngine;

/// <summary>
/// <see cref="IPodcastNarrator"/> backed by the audio-engine service: one m4b per call, narrated by
/// audio-engine's OpenAI or Chatterbox engine. Settings are re-read on every call, so a change in
/// Settings applies to the next narration. Every user-facing failure message starts with "audio-engine".
/// </summary>
public sealed class AudioEngineNarrator : IPodcastNarrator
{
    private const string UnauthorizedText = "audio-engine rejected the API key. Set it in Settings -> Podcast narration.";
    private const string NotConfiguredText = "audio-engine has no address set. Set it in Settings -> Podcast narration.";
    private const string UrlSeparator = "/";

    private const string PublicationPending = "pending";

    /// <summary>Job metadata: the reading list an episode was made from.</summary>
    private const string ListMetadataKey = "readingListId";

    /// <summary>Job metadata: the series (routine) an episode belongs to (workspace-wxll0.5).</summary>
    private const string SeriesMetadataKey = "wirecopySeries";

    private static readonly TimeSpan PublicationPoll = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PublicationTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan CancelTimeout = TimeSpan.FromSeconds(10);

    /// <summary>How long audio-engine may stay silent mid-job before the narration is given up.</summary>
    private static readonly TimeSpan OutageLimit = TimeSpan.FromMinutes(5);

    /// <summary>Pause between tries while audio-engine is not answering.</summary>
    private static readonly TimeSpan OutageRetryDelay = TimeSpan.FromSeconds(5);

    /// <summary>How long a local audio-engine may stay silent before WireCopy starts it again itself.</summary>
    private static readonly TimeSpan OutageRestartAfter = TimeSpan.FromSeconds(45);

    /// <summary>How often a job that waits its turn looks at the jobs ahead of it (workspace-wxll0.5).</summary>
    private static readonly TimeSpan QueuePoll = TimeSpan.FromSeconds(15);

    /// <summary>How long a cancelled job is given to leave audio-engine's lane.</summary>
    private static readonly TimeSpan SupersedeSettle = TimeSpan.FromSeconds(15);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IUserSettingsStore _store;
    private readonly AudioEngineConfiguration _config;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AudioEngineNarrator> _logger;
    private readonly Func<string, string?> _getEnv;
    private readonly IAudioEngineLauncher? _launcher;

    /// <summary>Reading list id to the series its episode belongs to, until that episode has been narrated.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _seriesByList = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new instance of the <see cref="AudioEngineNarrator"/> class that can start a
    /// local audio-engine again when it goes quiet in the middle of a job.
    /// </summary>
    /// <param name="store">The user settings store (address, key and voice settings).</param>
    /// <param name="options">The configured defaults.</param>
    /// <param name="httpClientFactory">Creates the "AudioEngine" HTTP client.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="launcher">Starts a local audio-engine.</param>
    public AudioEngineNarrator(
        IUserSettingsStore store,
        IOptions<AudioEngineConfiguration> options,
        IHttpClientFactory httpClientFactory,
        ILogger<AudioEngineNarrator> logger,
        IAudioEngineLauncher launcher)
        : this(store, options, httpClientFactory, logger, Environment.GetEnvironmentVariable)
    {
        _launcher = launcher;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AudioEngineNarrator"/> class.
    /// </summary>
    /// <param name="store">The user settings store (address, key and voice settings).</param>
    /// <param name="options">The configured defaults.</param>
    /// <param name="httpClientFactory">Creates the "AudioEngine" HTTP client.</param>
    /// <param name="logger">The logger.</param>
    public AudioEngineNarrator(
        IUserSettingsStore store,
        IOptions<AudioEngineConfiguration> options,
        IHttpClientFactory httpClientFactory,
        ILogger<AudioEngineNarrator> logger)
        : this(store, options, httpClientFactory, logger, Environment.GetEnvironmentVariable)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AudioEngineNarrator"/> class with an injectable
    /// environment reader (for tests).
    /// </summary>
    /// <param name="store">The user settings store.</param>
    /// <param name="options">The configured defaults.</param>
    /// <param name="httpClientFactory">Creates the "AudioEngine" HTTP client.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="getEnv">Reads an environment variable.</param>
    internal AudioEngineNarrator(
        IUserSettingsStore store,
        IOptions<AudioEngineConfiguration> options,
        IHttpClientFactory httpClientFactory,
        ILogger<AudioEngineNarrator> logger,
        Func<string, string?> getEnv)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(getEnv);

        _store = store;
        _config = options.Value;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _getEnv = getEnv;
    }

    /// <inheritdoc />
    public bool IsConfigured => IsReady(CurrentSettings());

    /// <inheritdoc />
    public async Task<NarratorStatus> CheckAsync(CancellationToken ct = default)
    {
        var s = CurrentSettings();
        var url = s.Url ?? string.Empty;
        var baseStatus = new NarratorStatus { Url = url };

        if (string.IsNullOrEmpty(s.Url))
        {
            return baseStatus with
            {
                ErrorKind = NarrationErrorKind.NotConfigured,
                Message = NotConfiguredText,
            };
        }

        Session? session = null;
        try
        {
            session = OpenSession(s);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(CheckTimeout);

            // 1. Is anything listening? /readyz is public, so no Authorization header.
            try
            {
                using var ready = NewRequest(HttpMethod.Get, "readyz", null);
                using var readyResponse = await session.Http.SendAsync(ready, cts.Token).ConfigureAwait(false);
                if (!readyResponse.IsSuccessStatusCode)
                {
                    var notReady = await NotReadyMessageAsync(readyResponse, url, cts.Token).ConfigureAwait(false);
                    return baseStatus with
                    {
                        ErrorKind = NarrationErrorKind.Transient,
                        Reachable = true,
                        Message = notReady,
                    };
                }
            }
            catch (Exception ex) when (!IsCallerCancel(ex, ct))
            {
                _logger.LogDebug(ex, "audio-engine /readyz probe failed at {Url}", url);
                return baseStatus with
                {
                    ErrorKind = NarrationErrorKind.Unreachable,
                    Message = UnreachableMessage(url),
                };
            }

            baseStatus = baseStatus with { Reachable = true };

            // 2. Does audio-engine accept the key, and is the engine it is set to use ready?
            List<EngineDto> dtos;
            try
            {
                using var request = NewRequest(HttpMethod.Get, "v1/engines", s.ApiKey);
                using var response = await session.Http.SendAsync(request, cts.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    var failure = await FailureFromResponseAsync(response, s, cts.Token).ConfigureAwait(false);
                    return baseStatus with
                    {
                        Authorized = failure.Kind != NarrationErrorKind.Unauthorized && baseStatus.Authorized,
                        ErrorKind = failure.Kind,
                        Message = failure.Message,
                    };
                }

                dtos = await response.Content.ReadFromJsonAsync<List<EngineDto>>(Json, cts.Token).ConfigureAwait(false) ?? [];
            }
            catch (Exception ex) when (!IsCallerCancel(ex, ct))
            {
                _logger.LogDebug(ex, "audio-engine /v1/engines probe failed at {Url}", url);
                var failure = Describe(ex, s);
                return baseStatus with { ErrorKind = failure.Kind, Message = failure.Message };
            }

            var engines = dtos
                .Select(d => new NarratorEngine(d.Name ?? string.Empty, d.IsConfigured, d.IsDefault))
                .ToList();
            var active = engines.Find(e => e.IsDefault);
            baseStatus = baseStatus with { Authorized = true, Engines = engines, EngineName = active?.Name ?? string.Empty };

            if (active is null || !active.IsConfigured)
            {
                return baseStatus with
                {
                    EngineConfigured = false,
                    ErrorKind = NarrationErrorKind.EngineNotConfigured,
                    Message = EngineNotConfiguredMessage(active?.Name, url),
                };
            }

            return baseStatus with
            {
                Ready = true,
                EngineConfigured = true,
                ErrorKind = NarrationErrorKind.None,
                Message = $"audio-engine is ready at {url} (narrating with {active.Name}).",
            };
        }
        catch (Exception ex) when (!IsCallerCancel(ex, ct))
        {
            var failure = Describe(ex, s);
            return baseStatus with { ErrorKind = failure.Kind, Message = failure.Message };
        }
        finally
        {
            session?.Dispose();
        }
    }

    /// <inheritdoc />
    public async Task<NarrationEstimate> EstimateAsync(NarrationRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var s = CurrentSettings();
        if (!IsReady(s))
        {
            return new NarrationEstimate { ErrorKind = NarrationErrorKind.NotConfigured, ErrorMessage = NotConfiguredText };
        }

        Session? session = null;
        try
        {
            session = OpenSession(s);
            var fingerprint = await EngineFingerprintAsync(session.Client, ct).ConfigureAwait(false)
                + DefaultFeedScope(await GetDefaultFeedAsync(ct).ConfigureAwait(false));
            var estimate = await session.Client.EstimateAsync(BuildJobRequest(request, fingerprint), ct).ConfigureAwait(false);
            return new NarrationEstimate
            {
                Success = true,
                EstimateUsd = estimate.EstimateUsd,
                FullEstimateUsd = estimate.FullEstimateUsd,
                EstimatedDurationSeconds = estimate.EstimatedDurationSeconds,
                BudgetAllowed = estimate.Budget?.Allowed ?? true,
                BudgetReason = estimate.Budget?.Reason,
                BudgetLimitUsd = estimate.Budget?.LimitUsd,
                EngineName = estimate.Engine,
                EngineIsLocal = estimate.EngineConfigured && estimate.FullEstimateUsd == 0m,
                Chapters = estimate.Chapters
                    .Select(c => new NarrationChapterEstimate(c.Index, c.Title, c.ChunksTotal, c.ChunksCached, c.EstimateUsd))
                    .ToList(),
            };
        }
        catch (Exception ex) when (!IsCallerCancel(ex, ct))
        {
            var failure = Describe(ex, s);
            _logger.LogWarning(ex, "audio-engine estimate failed: {Message}", failure.Message);
            return new NarrationEstimate { ErrorKind = failure.Kind, ErrorMessage = failure.Message };
        }
        finally
        {
            session?.Dispose();
        }
    }

    /// <inheritdoc />
    public async Task<NarrationPublication?> WaitForPublicationAsync(string jobId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        var s = CurrentSettings();
        if (!IsReady(s))
        {
            return null;
        }

        using var session = OpenSession(s);
        var deadline = DateTime.UtcNow + PublicationTimeout;
        NarrationPublication? last = null;
        while (true)
        {
            try
            {
                var job = await session.Client.GetAsync(jobId, ct).ConfigureAwait(false);
                last = MapPublication(job.Publication);
            }
            catch (Exception ex) when (!IsCallerCancel(ex, ct))
            {
                _logger.LogWarning(ex, "Could not read publication state of audio-engine job {JobId}", jobId);
                if (last is null)
                {
                    return null;
                }
            }

            if (last is null || last.Status != PublicationPending || DateTime.UtcNow >= deadline)
            {
                return last;
            }

            await Task.Delay(PublicationPoll, ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<NarrationFeed?> GetDefaultFeedAsync(CancellationToken ct = default)
    {
        var s = CurrentSettings();
        if (!IsReady(s))
        {
            return null;
        }

        try
        {
            using var session = OpenSession(s);

            // The SDK's Feed record lacks IsDefault, so read GET v1/feeds directly.
            using var request = NewRequest(HttpMethod.Get, "v1/feeds", s.ApiKey);
            using var response = await session.Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var failure = await FailureFromResponseAsync(response, s, ct).ConfigureAwait(false);
                _logger.LogWarning("Could not read audio-engine's default feed: {Message}", failure.Message);
                return null;
            }

            var list = await response.Content.ReadFromJsonAsync<FeedListDto>(Json, ct).ConfigureAwait(false);
            var def = list?.Items?.FirstOrDefault(f => f.IsDefault);
            return def is null ? null : new NarrationFeed(def.Name ?? string.Empty, def.Title ?? def.Name ?? string.Empty, def.SubscribeUrl);
        }
        catch (Exception ex) when (!IsCallerCancel(ex, ct))
        {
            _logger.LogWarning(ex, "Could not read audio-engine's default feed");
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<int> SupersedeEarlierEpisodesAsync(NarrationSeries series, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(series);
        if (string.IsNullOrWhiteSpace(series.Key))
        {
            return 0;
        }

        if (!string.IsNullOrWhiteSpace(series.ListId))
        {
            _seriesByList[series.ListId] = series.Key;
        }

        var s = CurrentSettings();
        if (!IsReady(s))
        {
            return 0;
        }

        var cancelled = new List<string>();
        try
        {
            using var session = OpenSession(s);
            foreach (var status in new[] { JobStatuses.Running, JobStatuses.Pending })
            {
                var page = await session.Client.ListAsync(status: status, limit: 50, ct: ct).ConfigureAwait(false);
                foreach (var job in page.Items ?? [])
                {
                    if (!IsEarlierEpisode(job, series))
                    {
                        continue;
                    }

                    try
                    {
                        await session.Client.CancelAsync(job.Id, ct).ConfigureAwait(false);
                        cancelled.Add(job.Id);
                        _logger.LogInformation(
                            "audio-engine was still narrating an earlier episode of this routine (“{Title:l}”, {Done} of {Total} parts, sent {Sent:u}): cancelled it, because this run replaces it",
                            job.Title,
                            job.ChunksDone,
                            job.ChunksTotal,
                            job.CreatedAt.UtcDateTime);
                    }
                    catch (AudioEngineApiException ex) when (ex.StatusCode == (int)HttpStatusCode.Conflict)
                    {
                        // It finished in the meantime: nothing to cancel.
                    }
                }
            }

            // A running job leaves the lane a moment after it is told to: wait for that, so what
            // comes next (the check for an older engine, this run's own job) finds the lane free.
            var settle = System.Diagnostics.Stopwatch.StartNew();
            while (cancelled.Count > 0 && settle.Elapsed < SupersedeSettle)
            {
                var busy = false;
                foreach (var id in cancelled)
                {
                    busy |= !(await session.Client.GetAsync(id, ct).ConfigureAwait(false)).IsTerminal;
                }

                if (!busy)
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (!IsCallerCancel(ex, ct))
        {
            // Not running, or not answering: there is nothing of ours in its lane that this run can reach.
            _logger.LogDebug(ex, "Could not look in audio-engine for earlier episodes of {Series}", series.Key);
        }

        return cancelled.Count;
    }

    /// <inheritdoc />
    public async Task<NarrationResult> NarrateAsync(
        NarrationRequest request,
        IProgress<NarrationProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var s = CurrentSettings();
        if (!IsReady(s))
        {
            return new NarrationResult { ErrorKind = NarrationErrorKind.NotConfigured, ErrorMessage = NotConfiguredText };
        }

        Session? session = null;
        string? jobId = null;
        try
        {
            session = OpenSession(s);
            var client = session.Client;

            var fingerprint = await EngineFingerprintAsync(client, ct).ConfigureAwait(false)
                + DefaultFeedScope(await GetDefaultFeedAsync(ct).ConfigureAwait(false));
            var created = await client.SubmitAsync(BuildJobRequest(request, fingerprint), ct).ConfigureAwait(false);
            jobId = created.Id;

            // The same reading list + settings + text maps to the same job. If that job already died,
            // re-queue it (its cached chunks make the retry cheap) instead of waiting on a corpse.
            if (created.Replayed)
            {
                var existing = await client.GetAsync(jobId, ct).ConfigureAwait(false);
                if (existing.Status is JobStatuses.Failed or JobStatuses.Cancelled or JobStatuses.Interrupted)
                {
                    await client.RetryAsync(jobId, ct).ConfigureAwait(false);
                }
            }

            var job = await WaitThroughOutagesAsync(client, jobId, s.Url, progress, ct).ConfigureAwait(false);
            if (!job.Succeeded)
            {
                return JobFailureResult(job);
            }

            var directory = Path.GetDirectoryName(Path.GetFullPath(request.OutputPath));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var bytes = await client.DownloadAsync(job.Id, request.OutputPath, ct).ConfigureAwait(false);
            return new NarrationResult
            {
                Success = true,
                OutputPath = request.OutputPath,
                Bytes = bytes,
                Duration = TimeSpan.FromSeconds(job.DurationSeconds ?? 0),
                CostUsd = job.EstCostUsd,
                SkippedChapters = job.SkippedChapters?
                    .Select(c => new NarrationSkippedChapter(c.Index, c.Title, c.Error))
                    .ToList() ?? [],
                JobId = job.Id,
                Publication = MapPublication(job.Publication),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (jobId is not null && session is not null)
            {
                await TryCancelJobAsync(session.Client, jobId).ConfigureAwait(false);
            }

            throw;
        }
        catch (Exception ex)
        {
            var failure = Describe(ex, s);
            _logger.LogWarning(ex, "audio-engine narration failed: {Message}", failure.Message);
            return new NarrationResult { ErrorKind = failure.Kind, ErrorMessage = failure.Message, JobId = jobId };
        }
        finally
        {
            session?.Dispose();
            if (!string.IsNullOrEmpty(request.ListId))
            {
                _seriesByList.TryRemove(request.ListId, out _);
            }
        }
    }

    /// <summary>
    /// Whether an unfinished audio-engine job is an earlier episode of a series: a job WireCopy sent
    /// (it carries a reading list id) for another reading list, marked with the same series - or,
    /// when it was sent before jobs carried the series, titled like one of the series' episodes.
    /// </summary>
    /// <param name="job">The job as audio-engine lists it.</param>
    /// <param name="series">The series and the episode about to be narrated.</param>
    /// <returns>True when the job may be cancelled in favour of the new episode.</returns>
    internal static bool IsEarlierEpisode(Job job, NarrationSeries series)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(series);
        var metadata = job.Metadata;
        if (metadata is null
            || !metadata.TryGetValue(ListMetadataKey, out var list)
            || string.Equals(list, series.ListId, StringComparison.OrdinalIgnoreCase))
        {
            // Not sent by WireCopy, or the very episode this run is about to narrate.
            return false;
        }

        if (metadata.TryGetValue(SeriesMetadataKey, out var key))
        {
            return string.Equals(key, series.Key, StringComparison.Ordinal);
        }

        return !string.IsNullOrWhiteSpace(series.EpisodeNamePrefix)
            && job.Title is { } title
            && title.Contains(": " + series.EpisodeNamePrefix, StringComparison.Ordinal);
    }

    /// <summary>
    /// Builds the audio-engine job for a request: chapters, tags, cover art and an idempotency key
    /// that changes whenever anything affecting the audio changes. Engine, voice and style are never
    /// sent: audio-engine decides them.
    /// </summary>
    /// <param name="request">What to narrate.</param>
    /// <param name="engineFingerprint">The settings fingerprint of the engine audio-engine will use.</param>
    /// <returns>The job request to submit or price.</returns>
    internal JobRequest BuildJobRequest(NarrationRequest request, string engineFingerprint)
    {
        ArgumentNullException.ThrowIfNull(request);

        string? coverArt = null;
        var coverHash = string.Empty;
        if (!string.IsNullOrWhiteSpace(request.CoverArtPath))
        {
            coverArt = TryReadCoverArt(request.CoverArtPath, out coverHash);
        }

        return new JobRequest
        {
            Title = request.Title,
            Chapters = request.Chapters.Select(c => new ChapterInput(c.Title, c.Text)).ToList(),
            Format = "m4b",
            Feed = JobRequest.DefaultFeed,
            Author = request.Author,
            Album = request.Album,
            Description = request.Description,
            Genre = request.Genre,
            CoverArt = coverArt,
            OnChapterError = "skip",
            Metadata = BuildMetadata(request.ListId),
            IdempotencyKey = BuildIdempotencyKey(request, engineFingerprint, coverHash),
        };
    }

    private static NarrationPublication? MapPublication(Publication? p) =>
        p is null ? null : new NarrationPublication(p.Feed, p.Status, p.AudioUrl, p.Error);

    private static string BuildIdempotencyKey(NarrationRequest request, string engineFingerprint, string coverHash)
    {
        var parts = new List<string>
        {
            Part(request.ListId),
            Part(engineFingerprint),
            Part(request.Title),
            Part(request.Author),
            Part(request.Album),
            Part(request.Description),
            Part(request.Genre),
            Part(coverHash),
        };

        foreach (var chapter in request.Chapters)
        {
            parts.Add(Part(chapter.Title));
            parts.Add(Part(Sha256Hex(Encoding.UTF8.GetBytes(chapter.Text ?? string.Empty))));
        }

        return "wirecopy-" + Sha256Hex(Encoding.UTF8.GetBytes(string.Join("\n", parts)));
    }

    private static string Part(string? value)
    {
        value ??= string.Empty;
        return string.Create(CultureInfo.InvariantCulture, $"{value.Length}:{value}");
    }

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static bool IsReady(AudioEngineSettings s) => !string.IsNullOrEmpty(s.Url);

    private static bool IsCallerCancel(Exception ex, CancellationToken ct) => ex is OperationCanceledException && ct.IsCancellationRequested;

    // The engine answered /readyz but said no: name the failing checks instead of claiming it is down.
    private static async Task<string> NotReadyMessageAsync(HttpResponseMessage response, string? url, CancellationToken ct)
    {
        var prefix = $"audio-engine is running at {url} but isn't ready";
        var fallback = $"{prefix} (HTTP {(int)response.StatusCode})";
        try
        {
            var buffer = new byte[2048];
            using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var read = 0;
            while (read < buffer.Length)
            {
                var n = await stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
                if (n == 0)
                {
                    break;
                }

                read += n;
            }

            using var doc = JsonDocument.Parse(buffer.AsMemory(0, read));
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("checks", out var checks) ||
                checks.ValueKind != JsonValueKind.Object)
            {
                return fallback;
            }

            var failing = new List<string>();
            foreach (var check in checks.EnumerateObject())
            {
                var value = check.Value.ValueKind == JsonValueKind.String ? check.Value.GetString() : check.Value.ToString();
                if (!string.Equals(value, "ok", StringComparison.OrdinalIgnoreCase))
                {
                    failing.Add($"{check.Name} — {value}");
                }
            }

            if (failing.Count == 0)
            {
                return fallback;
            }

            var message = $"{prefix}: {string.Join("; ", failing)}";
            return message.Length > 220 ? message[..219] + "…" : message;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested || ex is not OperationCanceledException)
        {
            return fallback;
        }
    }

    private static string UnreachableMessage(string? url) =>
        $"audio-engine isn't running at {url}. Start it (audio-engine README: Run natively) or change its address in Settings.";

    private static string EngineNotConfiguredMessage(string? engine, string? url) =>
        string.IsNullOrEmpty(engine)
            ? $"audio-engine's narration engine is not ready. Finish setting it up in audio-engine's Settings at {url}."
            : $"audio-engine is set to narrate with {engine}, which is not ready. Finish setting it up in audio-engine's Settings at {url}.";

    private static async Task<string> EngineFingerprintAsync(AudioEngineClient client, CancellationToken ct)
    {
        var engines = await client.ListEnginesAsync(ct).ConfigureAwait(false);
        return engines.FirstOrDefault(e => e.IsDefault)?.SettingsFingerprint ?? string.Empty;
    }

    // Part of the idempotency key: a re-run after the default feed changed (or was first set) must be a new
    // job, or audio-engine replays the earlier job and its episode is never published to the new feed.
    private static string DefaultFeedScope(NarrationFeed? feed) => "|feed:" + (feed?.Name ?? string.Empty);

    private static Uri BuildBaseAddress(string? url)
    {
        if (!Uri.TryCreate((url ?? string.Empty).TrimEnd('/') + UrlSeparator, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new HttpRequestException($"'{url}' is not a valid http(s) address.");
        }

        return uri;
    }

    private static HttpRequestMessage NewRequest(HttpMethod method, string relative, string? apiKey)
    {
        var message = new HttpRequestMessage(method, relative);
        if (!string.IsNullOrEmpty(apiKey))
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        return message;
    }

    /// <summary>
    /// Whether a failure means "audio-engine is not answering right now" (refused, reset, dropped,
    /// a 5xx) rather than an answer about the job itself.
    /// </summary>
    /// <param name="ex">The failure.</param>
    /// <returns>True when waiting and trying again is the right response.</returns>
    private static bool IsOutage(Exception ex) => ex switch
    {
        AudioEngineApiException api => api.StatusCode >= 500,
        HttpRequestException or IOException or TimeoutException => true,
        OperationCanceledException => true, // a timed-out request; the caller rules out its own cancellation
        _ => false,
    };

    private static NarrationResult JobFailureResult(Job job)
    {
        var (kind, message) = job.Status switch
        {
            JobStatuses.Cancelled => (NarrationErrorKind.Cancelled, $"audio-engine: narration was {job.Status}; run it again."),
            JobStatuses.Interrupted => (NarrationErrorKind.Transient, $"audio-engine: narration was {job.Status}; run it again."),
            _ => (NarrationErrorKind.Failed, "audio-engine: " + (string.IsNullOrWhiteSpace(job.Error) ? "narration failed." : job.Error)),
        };
        return new NarrationResult { ErrorKind = kind, ErrorMessage = message, JobId = job.Id };
    }

    /// <summary>Maps an audio-engine HTTP failure to a user-facing kind and message; null when the status is not one we classify.</summary>
    private static Failure? Classify(int status, string? title, string? detail, string? problemType, AudioEngineSettings s)
    {
        if (status is 401 or 403)
        {
            return new Failure(
                NarrationErrorKind.Unauthorized,
                string.IsNullOrEmpty(s.ApiKey)
                    ? $"audio-engine at {s.Url} wants an API key. Set it in Settings -> Podcast narration."
                    : UnauthorizedText);
        }

        if (MentionsNotConfigured(problemType) || MentionsNotConfigured(title) || MentionsNotConfigured(detail))
        {
            return new Failure(NarrationErrorKind.EngineNotConfigured, EngineNotConfiguredMessage(null, s.Url));
        }

        var text = !string.IsNullOrWhiteSpace(detail) ? detail : title ?? string.Empty;
        if (status is 409 or 422)
        {
            return new Failure(NarrationErrorKind.Rejected, $"audio-engine refused the job: {text}");
        }

        if (status >= 500)
        {
            return new Failure(NarrationErrorKind.Transient, $"audio-engine error {status}: {text}");
        }

        return null;
    }

    private static bool MentionsNotConfigured(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        // "engine_not_configured", "Engine not configured", "not-configured" all compare equal once
        // everything but letters is dropped.
        var letters = new string(text.Where(char.IsLetter).ToArray());
        return letters.Contains("notconfigured", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ProblemDetail(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var name in new[] { "detail", "title" })
                {
                    if (doc.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(value.GetString()))
                    {
                        return value.GetString();
                    }
                }

                // The OpenAI-compatible endpoints (v1/audio/speech) answer {"error":{"message":"…"}}.
                if (doc.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
                    && error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(message.GetString()))
                {
                    return message.GetString();
                }
            }
        }
        catch (JsonException)
        {
            // Not JSON: the raw body is the detail.
        }

        return null;
    }

    private static async Task<Failure> FailureFromResponseAsync(HttpResponseMessage response, AudioEngineSettings s, CancellationToken ct)
    {
        var status = (int)response.StatusCode;
        var body = (await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Trim();
        var detail = ProblemDetail(body) ?? (body.Length > 0 ? body : response.ReasonPhrase ?? string.Empty);
        var failure = Classify(status, response.ReasonPhrase, detail, null, s);
        if (failure is not null)
        {
            return failure.Value;
        }

        return status >= 400 && status < 500
            ? new Failure(NarrationErrorKind.Rejected, $"audio-engine refused the job: {detail}")
            : new Failure(NarrationErrorKind.Failed, $"audio-engine error {status}: {detail}");
    }

    private static Failure Describe(Exception ex, AudioEngineSettings s)
    {
        switch (ex)
        {
            case BudgetExceededApiException budget:
                return new Failure(
                    NarrationErrorKind.BudgetExceeded,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"audio-engine: narration would cost ${budget.EstimateUsd:F2}, over the ${budget.LimitUsd:F2} limit."));
            case AudioEngineApiException api:
                return Classify(api.StatusCode, api.Title, api.Detail, api.ProblemType, s)
                    ?? new Failure(NarrationErrorKind.Failed, api.Message);
            case HttpRequestException:
                return new Failure(NarrationErrorKind.Unreachable, UnreachableMessage(s.Url));
            case IOException io:
                return new Failure(NarrationErrorKind.Transient, "audio-engine: " + io.Message);
            default:
                return new Failure(NarrationErrorKind.Failed, "audio-engine: " + ex.Message);
        }
    }

    private AudioEngineSettings CurrentSettings() => AudioEngineSettings.Resolve(_store, _config, _getEnv);

    private Session OpenSession(AudioEngineSettings s)
    {
        var baseAddress = BuildBaseAddress(s.Url);
        var http = _httpClientFactory.CreateClient("AudioEngine");
        try
        {
            http.BaseAddress = baseAddress;
            return new Session(http, s.ApiKey);
        }
        catch
        {
            http.Dispose();
            throw;
        }
    }

    private string? TryReadCoverArt(string path, out string hash)
    {
        hash = string.Empty;
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (!File.Exists(path) || extension is not (".png" or ".jpg" or ".jpeg"))
        {
            _logger.LogWarning("Cover art '{Path}' was left out: it must be an existing .png, .jpg or .jpeg file", path);
            return null;
        }

        try
        {
            var coverArt = JobRequest.CoverArtFromFile(path);
            hash = Sha256Hex(File.ReadAllBytes(path));
            return coverArt;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Cover art '{Path}' was left out: {Message}", path, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Waits for the job to end, riding out an audio-engine that stops answering (workspace-wxll0.4).
    /// Narration takes hours, the engine is a separate program that can be restarted under it, and it
    /// picks an unfinished job up again when it comes back — so one failed read must not end the run.
    /// The wait is given up only after <see cref="OutageLimit"/> of unbroken silence.
    /// </summary>
    /// <param name="client">The audio-engine client.</param>
    /// <param name="jobId">The job being waited on.</param>
    /// <param name="url">The audio-engine base address (to start a local one again).</param>
    /// <param name="progress">Receives progress and the "not answering" notice.</param>
    /// <param name="ct">Cancels the wait.</param>
    /// <returns>The job in its final state.</returns>
    private async Task<Job> WaitThroughOutagesAsync(
        AudioEngineClient client, string jobId, string? url, IProgress<NarrationProgress>? progress, CancellationToken ct)
    {
        var sink = new ProgressSink(progress);

        // While the job waits its turn, the jobs ahead of it are what moves (workspace-wxll0.5).
        using var queueWatch = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var queue = WatchQueueAsync(client, jobId, sink, queueWatch.Token);
        try
        {
            return await WaitForJobAsync(client, jobId, url, sink, ct).ConfigureAwait(false);
        }
        finally
        {
            await queueWatch.CancelAsync().ConfigureAwait(false);
            try
            {
                await queue.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Stopped with the wait.
            }
        }
    }

    private async Task<Job> WaitForJobAsync(
        AudioEngineClient client, string jobId, string? url, ProgressSink sink, CancellationToken ct)
    {
        System.Diagnostics.Stopwatch? silentFor = null;
        var restartAsked = false;
        while (true)
        {
            try
            {
                // A plain read first: it tells an engine that is back from one that is still gone,
                // and returns at once a job that finished while nobody was listening.
                var job = await client.GetAsync(jobId, ct).ConfigureAwait(false);
                if (silentFor is not null)
                {
                    _logger.LogInformation(
                        "audio-engine is answering again after {Seconds:F0} s; carrying on with job {JobId} ({Status:l})",
                        silentFor.Elapsed.TotalSeconds,
                        jobId,
                        job.Status);
                    silentFor = null;
                    restartAsked = false;
                }

                if (job.IsTerminal)
                {
                    return job;
                }

                return await client.WaitAsync(jobId, sink, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsOutage(ex) && !ct.IsCancellationRequested)
            {
                if (silentFor is null)
                {
                    silentFor = System.Diagnostics.Stopwatch.StartNew();
                    _logger.LogWarning(
                        "audio-engine stopped answering while narrating job {JobId} ({Message:l}); waiting up to {Minutes:F0} minutes for it",
                        jobId,
                        ex.Message,
                        OutageLimit.TotalMinutes);
                }
                else if (silentFor.Elapsed >= OutageLimit)
                {
                    _logger.LogWarning(
                        "audio-engine has not answered for {Minutes:F0} minutes; giving up on job {JobId}",
                        silentFor.Elapsed.TotalMinutes,
                        jobId);
                    throw;
                }

                sink.ReportNotAnswering();
                if (!restartAsked && silentFor.Elapsed >= OutageRestartAfter && _launcher is not null && AudioEngineLauncher.IsLocal(url))
                {
                    restartAsked = true;
                    _logger.LogInformation("audio-engine has not answered for {Seconds:F0} s; starting it", silentFor.Elapsed.TotalSeconds);
                    var launcher = _launcher;
                    _ = Task.Run(
                        async () =>
                        {
                            try
                            {
                                await launcher.StartAndWaitAsync(url!, CancellationToken.None).ConfigureAwait(false);
                            }
                            catch (Exception startEx)
                            {
                                _logger.LogDebug(startEx, "Starting audio-engine again failed");
                            }
                        },
                        CancellationToken.None);
                }

                await Task.Delay(OutageRetryDelay, ct).ConfigureAwait(false);
            }
        }
    }

    private Dictionary<string, string>? BuildMetadata(string? listId)
    {
        if (string.IsNullOrEmpty(listId))
        {
            return null;
        }

        var metadata = new Dictionary<string, string> { [ListMetadataKey] = listId };
        if (_seriesByList.TryGetValue(listId, out var series))
        {
            metadata[SeriesMetadataKey] = series;
        }

        return metadata;
    }

    /// <summary>
    /// While a job is pending because audio-engine's lane is busy with other jobs, reports those
    /// jobs' progress as this job's "waiting its turn" state: the run view can say so, and a queue
    /// that moves is not mistaken for a run that stands still.
    /// </summary>
    private async Task WatchQueueAsync(AudioEngineClient client, string jobId, ProgressSink sink, CancellationToken ct)
    {
        string? behind = null;
        var waiting = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            try
            {
                var job = await client.GetAsync(jobId, ct).ConfigureAwait(false);
                if (job.IsTerminal)
                {
                    return;
                }

                var ahead = string.Equals(job.Status, JobStatuses.Pending, StringComparison.OrdinalIgnoreCase)
                    ? ((await client.ListAsync(status: JobStatuses.Running, limit: 20, ct: ct).ConfigureAwait(false)).Items ?? [])
                        .Where(j => !string.Equals(j.Id, jobId, StringComparison.Ordinal))
                        .ToList()
                    : [];
                if (ahead.Count > 0)
                {
                    var title = string.IsNullOrWhiteSpace(ahead[0].Title) ? "another job" : ahead[0].Title!;
                    var done = ahead.Sum(j => j.ChunksDone);
                    var total = ahead.Sum(j => j.ChunksTotal);
                    if (!string.Equals(behind, title, StringComparison.Ordinal))
                    {
                        _logger.LogInformation(
                            "audio-engine is narrating “{Title:l}” first ({Done} of {Total} parts done): this run's narration waits its turn",
                            title,
                            done,
                            total);
                        behind = title;
                    }

                    sink.ReportQueue(title, done, total);
                }
                else
                {
                    if (behind is not null)
                    {
                        _logger.LogInformation(
                            "This run's narration waited {Minutes:F0} min for its turn in audio-engine; it is being narrated now",
                            waiting.Elapsed.TotalMinutes);
                        behind = null;
                    }

                    sink.ReportQueue(null, 0, 0);
                    waiting.Restart();
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // The wait itself notices an engine that stopped answering; here it only means no news.
                _logger.LogDebug(ex, "Could not look at audio-engine's queue for job {JobId}", jobId);
            }

            await Task.Delay(QueuePoll, ct).ConfigureAwait(false);
        }
    }

    private async Task TryCancelJobAsync(AudioEngineClient client, string jobId)
    {
        try
        {
            // Deliberately not the caller's (already cancelled) token; bounded so Cancel never hangs.
            using var cts = new CancellationTokenSource(CancelTimeout);
            await client.CancelAsync(jobId, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not cancel audio-engine job {JobId} after the caller cancelled", jobId);
        }
    }

    private readonly record struct Failure(NarrationErrorKind Kind, string Message);

    private sealed record EngineDto(string? Name, bool IsConfigured, bool IsDefault);

    private sealed record FeedDto(string? Name, string? Title, string? SubscribeUrl, bool IsDefault);

    private sealed record FeedListDto(List<FeedDto>? Items);

    /// <summary>Reports each audio-engine event to the caller's callback synchronously, on the thread that raised it.</summary>
    private sealed class ProgressSink : IProgress<JobEvent>
    {
        private readonly IProgress<NarrationProgress>? _target;
        private readonly Lock _gate = new();

        private NarrationProgress? _last;
        private (string Title, int Done, int Total)? _queue;

        public ProgressSink(IProgress<NarrationProgress>? target) => _target = target;

        public void Report(JobEvent value)
        {
            lock (_gate)
            {
                // The job's own turn has come as soon as it is no longer pending.
                if (value.Status.Length > 0 && !string.Equals(value.Status, JobStatuses.Pending, StringComparison.OrdinalIgnoreCase))
                {
                    _queue = null;
                }

                _last = Map(value);
                _target?.Report(WithQueue(_last));
            }
        }

        /// <summary>Says audio-engine went quiet, keeping the numbers it last reported.</summary>
        public void ReportNotAnswering()
        {
            lock (_gate)
            {
                _target?.Report(WithQueue(Last()) with { EngineNotAnswering = true });
            }
        }

        /// <summary>Says which job is being narrated ahead of this one and how far it is; a null title ends the wait.</summary>
        public void ReportQueue(string? title, int done, int total)
        {
            lock (_gate)
            {
                var before = _queue;
                _queue = title is null ? null : (title, done, total);
                if (_queue != before)
                {
                    _target?.Report(WithQueue(Last()));
                }
            }
        }

        private static NarrationProgress Map(JobEvent e) => new()
        {
            Phase = Enum.TryParse<NarrationPhase>(e.Phase, ignoreCase: true, out var phase) && Enum.IsDefined(phase)
                ? phase
                : NarrationPhase.Synthesizing,
            Fraction = e.Progress,
            ChapterIndex = e.ChapterIndex,
            ChapterTitle = e.ChapterTitle,
            ChaptersTotal = e.ChaptersTotal,
            ChunksDone = e.ChunksDone,
            ChunksTotal = e.ChunksTotal,
            Message = e.Message,
            Remaining = e.EtaSeconds is > 0 and < 7 * 24 * 3600 ? TimeSpan.FromSeconds(e.EtaSeconds.Value) : null,
        };

        private NarrationProgress Last() => _last ?? new NarrationProgress { Phase = NarrationPhase.Synthesizing };

        private NarrationProgress WithQueue(NarrationProgress p) => _queue is { } q
            ? p with { WaitingBehind = q.Title, QueueDone = q.Done, QueueTotal = q.Total }
            : p;
    }

    /// <summary>One call's HTTP client plus the audio-engine client wrapped around it; disposes both.</summary>
    private sealed class Session : IDisposable
    {
        public Session(HttpClient http, string? apiKey)
        {
            Http = http;
            Client = new AudioEngineClient(http, apiKey);
        }

        public HttpClient Http { get; }

        public AudioEngineClient Client { get; }

        public void Dispose()
        {
            Client.Dispose();
            Http.Dispose();
        }
    }
}
