// Licensed under the MIT License. See LICENSE in the repository root.

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WireCopy.Infrastructure.Configuration;

namespace WireCopy.Infrastructure.Podcast.Chatterbox;

/// <summary>
/// Spawns and owns the Chatterbox worker child process. One process, one
/// in-flight request at a time (the worker is single-threaded anyway); the
/// object survives crashes/timeouts — the next <see cref="StartAsync"/> spawns fresh.
/// </summary>
internal sealed class ChatterboxSidecar : IChatterboxSidecar, IDisposable
{
    private const int StderrRingCapacity = 40;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly ChatterboxConfiguration _config;
    private readonly ILogger<ChatterboxSidecar> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Queue<string> _stderrRing = new();
    private readonly Lock _ringLock = new();

    private Process? _process;
    private Channel<WorkerReply>? _replies;
    private bool _ready;

    public ChatterboxSidecar(IOptions<ChatterboxConfiguration> config, ILogger<ChatterboxSidecar> logger)
    {
        _config = config.Value;
        _logger = logger;
    }

    public bool IsRunning => _ready && _process is { HasExited: false };

    /// <summary>
    /// Gets environment variables applied to the spawned worker. Test seam —
    /// lets tests inject the fake worker's FAKE_CB_* failure modes.
    /// </summary>
    internal IDictionary<string, string> EnvironmentOverrides { get; } = new Dictionary<string, string>();

    public async Task StartAsync(IProgress<string>? setupProgress, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsRunning)
            {
                return;
            }

            await StartLockedAsync(setupProgress, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ChatterboxSpeakResult> SpeakAsync(ChatterboxSpeakRequest request, IProgress<string>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!IsRunning)
            {
                throw new InvalidOperationException("Chatterbox worker is not running — call StartAsync first.");
            }

            var line = JsonSerializer.Serialize(new
            {
                cmd = "speak",
                id = request.Id,
                text = request.Text,
                sample_path = request.SamplePath,
                exaggeration = request.Exaggeration,
                cfg_weight = request.CfgWeight,
                device = _config.Device,
                out_path = request.OutPath,
            });

            try
            {
                await _process!.StandardInput.WriteLineAsync(line.AsMemory(), ct).ConfigureAwait(false);
                await _process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                _ready = false;
                throw new InvalidOperationException(
                    $"Chatterbox worker pipe closed while sending a request (exit code {TryGetExitCode()}). Last stderr:{Environment.NewLine}{RingSnapshot()}", ex);
            }

            // The per-chunk budget assumes a loaded model; the first speak may trigger
            // a model load (or first-run weight download), announced by a stage=load
            // progress line — extend the budget when that happens.
            var stopwatch = Stopwatch.StartNew();
            var budget = TimeSpan.FromSeconds(_config.SpeakTimeoutSecondsPerChunk);

            while (true)
            {
                var remaining = budget - stopwatch.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    return ThrowSpeakTimeout(budget);
                }

                WorkerReply reply;
                using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    timeoutCts.CancelAfter(remaining);
                    try
                    {
                        reply = await _replies!.Reader.ReadAsync(timeoutCts.Token).ConfigureAwait(false);
                    }
                    catch (ChannelClosedException ex)
                    {
                        _ready = false;
                        throw new InvalidOperationException(
                            $"Chatterbox worker exited with code {TryGetExitCode()} while a request was pending. Last stderr:{Environment.NewLine}{RingSnapshot()}", ex);
                    }
                    catch (OperationCanceledException)
                    {
                        if (ct.IsCancellationRequested)
                        {
                            // A generate() can't be interrupted politely — kill and stay restartable.
                            KillProcess();
                            ct.ThrowIfCancellationRequested();
                        }

                        return ThrowSpeakTimeout(budget);
                    }
                }

                if (reply.Event == "progress")
                {
                    if (reply.Stage == "load")
                    {
                        budget = TimeSpan.FromSeconds(_config.LoadTimeoutSeconds);
                        stopwatch.Restart();
                    }

                    if (!string.IsNullOrEmpty(reply.Message))
                    {
                        progress?.Report("Local narration: " + reply.Message);
                    }

                    continue;
                }

                if (reply.Id == request.Id && reply.Event is "spoken" or "error")
                {
                    return new ChatterboxSpeakResult(reply.Ok, reply.OutPath, reply.AudioSeconds, reply.Error);
                }

                _logger.LogWarning("[chatterbox] Unmatched worker reply (event={Event}, id={Id}) — ignoring", reply.Event, reply.Id);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync()
    {
        try
        {
            var process = _process;
            _ready = false;
            if (process is null || process.HasExited)
            {
                return;
            }

            try
            {
                await process.StandardInput.WriteLineAsync("{\"cmd\":\"shutdown\"}").ConfigureAwait(false);
                await process.StandardInput.FlushAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // Pipe already gone — fall through to kill.
            }

            using var graceCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try
            {
                await process.WaitForExitAsync(graceCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[chatterbox] StopAsync swallow — shutdown must never throw");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _process?.Dispose();
        _process = null;
        _gate.Dispose();
    }

    /// <summary>
    /// Synchronous dispose for containers/scopes that dispose synchronously
    /// (Microsoft.Extensions.DependencyInjection throws on IAsyncDisposable-only
    /// singletons). Skips the graceful shutdown handshake — straight kill.
    /// </summary>
    public void Dispose()
    {
        KillProcess();
        _process?.Dispose();
        _process = null;
        _gate.Dispose();
    }

    private static async Task WaitForEventAsync(ChannelReader<WorkerReply> reader, string eventName, CancellationToken ct)
    {
        while (true)
        {
            var reply = await reader.ReadAsync(ct).ConfigureAwait(false);
            if (reply.Event == eventName)
            {
                return;
            }
        }
    }

    private async Task StartLockedAsync(IProgress<string>? setupProgress, CancellationToken ct)
    {
        var workerPath = Path.Combine(AppContext.BaseDirectory, _config.WorkerRelativePath);
        if (!File.Exists(workerPath))
        {
            throw new InvalidOperationException($"Chatterbox worker script not found at {workerPath} — rebuild the app.");
        }

        _process?.Dispose();
        _ready = false;
        lock (_ringLock)
        {
            _stderrRing.Clear();
        }

        var arguments = string.IsNullOrWhiteSpace(_config.UvArgs)
            ? $"\"{workerPath}\""
            : $"{_config.UvArgs} \"{workerPath}\"";

        var psi = new ProcessStartInfo
        {
            FileName = _config.UvPath,
            Arguments = arguments,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var (key, value) in EnvironmentOverrides)
        {
            psi.Environment[key] = value;
        }

        _logger.LogInformation("[chatterbox] Spawning worker: {FileName} {Arguments}", psi.FileName, psi.Arguments);
        var process = new Process { StartInfo = psi };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException($"Failed to start '{psi.FileName}' — Process.Start returned false.");
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            process.Dispose();
            throw new InvalidOperationException(
                $"Could not launch the Chatterbox worker via '{psi.FileName}'. Is uv installed? ({ex.Message})", ex);
        }

        _process = process;
        var replies = Channel.CreateUnbounded<WorkerReply>(new UnboundedChannelOptions { SingleWriter = true });
        _replies = replies;
        _ = PumpStderrAsync(process, setupProgress);
        _ = PumpStdoutAsync(process, replies.Writer);

        using var startCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        startCts.CancelAfter(TimeSpan.FromSeconds(_config.StartTimeoutSeconds));
        try
        {
            // Ready banner, then a health round-trip proves the request loop is serving.
            await WaitForEventAsync(replies.Reader, "ready", startCts.Token).ConfigureAwait(false);
            await process.StandardInput.WriteLineAsync("{\"cmd\":\"health\"}".AsMemory(), startCts.Token).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(startCts.Token).ConfigureAwait(false);
            await WaitForEventAsync(replies.Reader, "health", startCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            KillProcess();
            throw new TimeoutException(
                $"Chatterbox worker did not become ready within {_config.StartTimeoutSeconds}s. Last stderr:{Environment.NewLine}{RingSnapshot()}");
        }
        catch (OperationCanceledException)
        {
            KillProcess();
            throw;
        }
        catch (ChannelClosedException ex)
        {
            throw new InvalidOperationException(
                $"Chatterbox worker exited during startup with code {TryGetExitCode()}. Last stderr:{Environment.NewLine}{RingSnapshot()}", ex);
        }

        _ready = true;
        _logger.LogInformation("[chatterbox] Worker ready (pid {Pid})", process.Id);
    }

    private ChatterboxSpeakResult ThrowSpeakTimeout(TimeSpan budget)
    {
        KillProcess();
        throw new TimeoutException(
            $"Chatterbox worker did not answer within the {(int)budget.TotalSeconds}s speak timeout — killed the worker. Last stderr:{Environment.NewLine}{RingSnapshot()}");
    }

    private async Task PumpStdoutAsync(Process process, ChannelWriter<WorkerReply> writer)
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                WorkerReply? reply = null;
                try
                {
                    reply = JsonSerializer.Deserialize<WorkerReply>(line, JsonOptions);
                }
                catch (JsonException)
                {
                    // Defensive: the workers reroute stray prints to stderr, but never
                    // let one bad line kill the protocol pump.
                }

                if (reply?.Event is null)
                {
                    _logger.LogWarning("[chatterbox] Non-protocol stdout line ignored: {Line}", line);
                    continue;
                }

                await writer.WriteAsync(reply).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // Stream torn down mid-read (kill/dispose) — fall through to complete the channel.
        }
        finally
        {
            _ready = false;
            writer.TryComplete();
        }
    }

    private async Task PumpStderrAsync(Process process, IProgress<string>? setupProgress)
    {
        try
        {
            while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (line.Length == 0)
                {
                    continue;
                }

                _logger.LogDebug("[chatterbox] {Line}", line);
                lock (_ringLock)
                {
                    _stderrRing.Enqueue(line);
                    while (_stderrRing.Count > StderrRingCapacity)
                    {
                        _stderrRing.Dequeue();
                    }
                }

                setupProgress?.Report(line);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // Stream torn down mid-read — nothing to do.
        }
    }

    private void KillProcess()
    {
        _ready = false;
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone — fine.
        }
    }

    private string RingSnapshot()
    {
        lock (_ringLock)
        {
            return _stderrRing.Count == 0 ? "(no stderr output)" : string.Join(Environment.NewLine, _stderrRing);
        }
    }

    private string TryGetExitCode()
    {
        try
        {
            // stdout EOF can race HasExited by a few ms — give the process a
            // moment to be reaped so failure messages carry the real code.
            return _process is not null && _process.WaitForExit(2000)
                ? _process.ExitCode.ToString()
                : "unknown";
        }
        catch (SystemException)
        {
            return "unknown";
        }
    }

    /// <summary>One protocol line from the worker (snake_case JSON).</summary>
    private sealed record WorkerReply(
        [property: JsonPropertyName("ok")] bool Ok,
        [property: JsonPropertyName("event")] string? Event,
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("out_path")] string? OutPath,
        [property: JsonPropertyName("audio_seconds")] double AudioSeconds,
        [property: JsonPropertyName("error")] string? Error,
        [property: JsonPropertyName("message")] string? Message,
        [property: JsonPropertyName("stage")] string? Stage);
}
