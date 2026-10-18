using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AudioEngine.Client;

/// <summary>
/// Typed client for the audio-engine job API. Submit a job, wait for it (server-sent events with a polling fallback),
/// then download the audio. Thread-safe; share one instance.
/// </summary>
public sealed class AudioEngineClient : IDisposable
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string? _apiKey;

    /// <summary>Use a caller-owned <see cref="HttpClient"/> (must have <see cref="HttpClient.BaseAddress"/> set).</summary>
    /// <param name="http">Configured client; it is not disposed by this class.</param>
    /// <param name="apiKey">Sent as <c>Authorization: Bearer</c> on every request when set.</param>
    public AudioEngineClient(HttpClient http, string? apiKey = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        if (http.BaseAddress is null)
        {
            throw new ArgumentException("HttpClient.BaseAddress must be set (for example http://127.0.0.1:5093/).", nameof(http));
        }

        _http = http;
        _apiKey = apiKey;
    }

    /// <summary>Create a client that owns its own <see cref="HttpClient"/>.</summary>
    public AudioEngineClient(string baseUrl, string? apiKey = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        _http = new HttpClient
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),

            // Downloads and event streams are long-lived; cancellation is the caller's token.
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _ownsHttp = true;
        _apiKey = apiKey;
    }

    /// <summary>How often <see cref="WaitAsync"/> polls when the event stream is unavailable. Default 1 second.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Submit a job. Returns 202 for a new job; <see cref="JobCreated.Replayed"/> is true when an idempotency key matched.</summary>
    public async Task<JobCreated> SubmitAsync(JobRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var msg = NewRequest(HttpMethod.Post, "v1/jobs");
        msg.Content = JsonContent.Create(request, options: Json);
        using var resp = await _http.SendAsync(msg, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(resp, ct).ConfigureAwait(false);
        var created = await resp.Content.ReadFromJsonAsync<JobCreated>(Json, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("audio-engine returned an empty body for POST /v1/jobs.");
        return created with { Replayed = resp.StatusCode == HttpStatusCode.OK };
    }

    /// <summary>Price and size a request (cost, cached chunks, duration, budget) without creating a job. Same body and errors as <see cref="SubmitAsync"/>.</summary>
    public async Task<JobEstimate> EstimateAsync(JobRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var msg = NewRequest(HttpMethod.Post, "v1/jobs/estimate");
        msg.Content = JsonContent.Create(request, options: Json);
        using var resp = await _http.SendAsync(msg, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(resp, ct).ConfigureAwait(false);
        return await resp.Content.ReadFromJsonAsync<JobEstimate>(Json, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("audio-engine returned an empty body for POST /v1/jobs/estimate.");
    }

    /// <summary>Fetch one job.</summary>
    public Task<Job> GetAsync(string id, CancellationToken ct = default) =>
        SendJsonAsync<Job>(HttpMethod.Get, $"v1/jobs/{Uri.EscapeDataString(id)}", ct);

    /// <summary>List jobs, newest first. Page by passing the previous <see cref="JobList.NextCursor"/>.</summary>
    public Task<JobList> ListAsync(string? status = null, string? query = null, int? limit = null, string? cursor = null, CancellationToken ct = default)
    {
        var parts = new List<string>();
        void Add(string key, string? value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                parts.Add($"{key}={Uri.EscapeDataString(value)}");
            }
        }

        Add("status", status);
        Add("q", query);
        Add("limit", limit?.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Add("cursor", cursor);
        var qs = parts.Count == 0 ? "" : "?" + string.Join('&', parts);
        return SendJsonAsync<JobList>(HttpMethod.Get, "v1/jobs" + qs, ct);
    }

    /// <summary>Cancel a pending or running job (409 if it already finished).</summary>
    public Task<Job> CancelAsync(string id, CancellationToken ct = default) =>
        SendJsonAsync<Job>(HttpMethod.Post, $"v1/jobs/{Uri.EscapeDataString(id)}/cancel", ct);

    /// <summary>Re-queue a failed, cancelled or interrupted job (409 otherwise).</summary>
    public Task<Job> RetryAsync(string id, CancellationToken ct = default) =>
        SendJsonAsync<Job>(HttpMethod.Post, $"v1/jobs/{Uri.EscapeDataString(id)}/retry", ct);

    /// <summary>Delete a job and its audio.</summary>
    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        using var msg = NewRequest(HttpMethod.Delete, $"v1/jobs/{Uri.EscapeDataString(id)}");
        using var resp = await _http.SendAsync(msg, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(resp, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Wait until the job reaches a terminal status (succeeded, failed, cancelled or interrupted) and return it.
    /// Uses the SSE stream, reporting each event to <paramref name="progress"/>; if the stream cannot be opened or
    /// keeps dropping it falls back to polling <see cref="GetAsync"/> every <see cref="PollInterval"/>.
    /// A failed job is returned, not thrown: check <see cref="Job.Succeeded"/> and <see cref="Job.Error"/>.
    /// </summary>
    public async Task<Job> WaitAsync(string id, IProgress<JobEvent>? progress = null, CancellationToken ct = default)
    {
        var sseFailures = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var sawTerminal = false;
            var polled = false;
            if (sseFailures < 3)
            {
                try
                {
                    sawTerminal = await PumpEventsAsync(id, progress, ct).ConfigureAwait(false);
                }
                catch (AudioEngineApiException ex) when (ex.StatusCode == 404)
                {
                    throw;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or AudioEngineApiException
                                           || (ex is OperationCanceledException && !ct.IsCancellationRequested))
                {
                    sseFailures++;
                }
            }
            else
            {
                polled = true;
            }

            var job = await GetAsync(id, ct).ConfigureAwait(false);
            if (job.IsTerminal)
            {
                return job;
            }

            if (polled)
            {
                progress?.Report(new JobEvent
                {
                    JobId = job.Id,
                    Type = "job.progress",
                    Status = job.Status,
                    Phase = job.Phase,
                    Progress = job.Progress,
                    ChunksDone = job.ChunksDone,
                    ChunksTotal = job.ChunksTotal,
                    Timestamp = DateTimeOffset.UtcNow,
                });
            }

            if (!sawTerminal)
            {
                await Task.Delay(PollInterval, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Download the finished audio to <paramref name="path"/> (written atomically). Returns the byte count.</summary>
    public async Task<long> DownloadAsync(string id, string path, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var msg = NewRequest(HttpMethod.Get, $"v1/jobs/{Uri.EscapeDataString(id)}/audio");
        using var resp = await _http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(resp, ct).ConfigureAwait(false);

        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temp = full + ".part";
        long written;
        await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var dst = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            await src.CopyToAsync(dst, ct).ConfigureAwait(false);
            written = dst.Length;
        }

        File.Move(temp, full, overwrite: true);
        return written;
    }

    /// <summary>Stream one job's events (snapshot first), ending when the server closes the stream. Yields every event.</summary>
    public async IAsyncEnumerable<JobEvent> StreamEventsAsync(string id, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        using var msg = NewRequest(HttpMethod.Get, $"v1/jobs/{Uri.EscapeDataString(id)}/events");
        msg.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var resp = await _http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(resp, ct).ConfigureAwait(false);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        var data = new System.Text.StringBuilder();
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    var payload = data.ToString();
                    data.Clear();
                    JobEvent? evt = null;
                    try
                    {
                        evt = JsonSerializer.Deserialize<JobEvent>(payload, Json);
                    }
                    catch (JsonException)
                    {
                        // Ignore a malformed frame rather than abandoning the stream.
                    }

                    if (evt is not null)
                    {
                        yield return evt;
                    }
                }

                continue;
            }

            if (line[0] == ':')
            {
                continue; // comment / heartbeat ("ping")
            }

            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0)
                {
                    data.Append('\n');
                }

                data.Append(line.AsSpan(5).TrimStart(' '));
            }
        }
    }

    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }

    private async Task<bool> PumpEventsAsync(string id, IProgress<JobEvent>? progress, CancellationToken ct)
    {
        await foreach (var evt in StreamEventsAsync(id, ct).ConfigureAwait(false))
        {
            progress?.Report(evt);
            if (evt.IsTerminal)
            {
                return true;
            }
        }

        return false;
    }

    private HttpRequestMessage NewRequest(HttpMethod method, string relative)
    {
        var msg = new HttpRequestMessage(method, new Uri(relative, UriKind.Relative));
        if (!string.IsNullOrEmpty(_apiKey))
        {
            msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        }

        return msg;
    }

    private async Task<T> SendJsonAsync<T>(HttpMethod method, string relative, CancellationToken ct)
    {
        using var msg = NewRequest(method, relative);
        using var resp = await _http.SendAsync(msg, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(resp, ct).ConfigureAwait(false);
        return await resp.Content.ReadFromJsonAsync<T>(Json, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"audio-engine returned an empty body for {method} {relative}.");
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode)
        {
            return;
        }

        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        throw ParseError((int)resp.StatusCode, resp.ReasonPhrase, body);
    }

    internal static AudioEngineApiException ParseError(int status, string? reason, string body)
    {
        string? title = reason;
        string? detail = null;
        string? type = null;
        var ext = new Dictionary<string, JsonElement>();
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    switch (prop.Name)
                    {
                        case "title" when prop.Value.ValueKind == JsonValueKind.String:
                            title = prop.Value.GetString();
                            break;
                        case "detail" when prop.Value.ValueKind == JsonValueKind.String:
                            detail = prop.Value.GetString();
                            break;
                        case "type" when prop.Value.ValueKind == JsonValueKind.String:
                            type = prop.Value.GetString();
                            break;
                        case "status":
                            break;
                        case "error" when prop.Value.ValueKind == JsonValueKind.Object:
                            // OpenAI-style {"error":{"message":..}} from /v1/audio/speech.
                            if (prop.Value.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                            {
                                detail = m.GetString();
                            }

                            break;
                        default:
                            ext[prop.Name] = prop.Value.Clone();
                            break;
                    }
                }
            }
        }
        catch (JsonException)
        {
            detail = string.IsNullOrWhiteSpace(body) ? null : body.Length > 500 ? body[..500] : body;
        }

        if (status == 402 && ext.TryGetValue("estimateUsd", out var est) && ext.TryGetValue("limitUsd", out var lim)
            && est.TryGetDecimal(out var e) && lim.TryGetDecimal(out var l))
        {
            var why = ext.TryGetValue("reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
            return new BudgetExceededApiException(title, detail, type, body, ext, e, l, why);
        }

        return new AudioEngineApiException(status, title, detail, type, body, ext);
    }
}
