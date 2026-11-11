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

    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan CancelTimeout = TimeSpan.FromSeconds(10);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IUserSettingsStore _store;
    private readonly AudioEngineConfiguration _config;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AudioEngineNarrator> _logger;
    private readonly Func<string, string?> _getEnv;

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
            var fingerprint = await EngineFingerprintAsync(session.Client, ct).ConfigureAwait(false);
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

            var fingerprint = await EngineFingerprintAsync(client, ct).ConfigureAwait(false);
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

            var job = await client.WaitAsync(jobId, new ProgressSink(progress), ct).ConfigureAwait(false);
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
        }
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
            Metadata = string.IsNullOrEmpty(request.ListId)
                ? null
                : new Dictionary<string, string> { ["readingListId"] = request.ListId },
            IdempotencyKey = BuildIdempotencyKey(request, engineFingerprint, coverHash),
        };
    }

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

    /// <summary>Reports each audio-engine event to the caller's callback synchronously, on the thread that raised it.</summary>
    private sealed class ProgressSink : IProgress<JobEvent>
    {
        private readonly IProgress<NarrationProgress>? _target;

        public ProgressSink(IProgress<NarrationProgress>? target) => _target = target;

        public void Report(JobEvent value) => _target?.Report(Map(value));

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
        };
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
