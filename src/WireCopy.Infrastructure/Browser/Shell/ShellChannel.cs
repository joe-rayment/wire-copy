// Licensed under the MIT License. See LICENSE in the repository root.

using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WireCopy.Application.Interfaces.Browser;

namespace WireCopy.Infrastructure.Browser.Shell;

/// <summary>
/// Live connection to the desktop shell over the Unix-domain socket the shell exports via
/// WIRECOPY_SHELL_CHANNEL. One JSON object per line: requests are id-correlated
/// ({id, method, params} → {id, ok, result|error}); shell-pushed events carry {event, params}.
/// Connection loss is terminal for the channel: pending and future requests fail soft
/// (false/null) and the loss is logged once — the app keeps running as a plain terminal app.
/// </summary>
public sealed class ShellChannel : IShellChannel, IAsyncDisposable
{
    /// <summary>Environment variable carrying the shell's socket path.</summary>
    public const string EnvVar = "WIRECOPY_SHELL_CHANNEL";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    private readonly NetworkStream _stream;
    private readonly StreamWriter _writer;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement?>> _pending = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _endpointGate = new();
    private Task<string?>? _endpointTask;
    private long _nextId;
    private volatile bool _dead;

    private ShellChannel(Socket socket, ILogger logger)
    {
        _stream = new NetworkStream(socket, ownsSocket: true);
        _writer = new StreamWriter(_stream) { AutoFlush = true };
        _logger = logger;
        _ = Task.Run(() => ReadLoopAsync(_lifetime.Token));
    }

    /// <inheritdoc />
    public event Action<string>? ModeChanged;

    /// <inheritdoc />
    public bool IsConnected => !_dead;

    /// <summary>
    /// Connects to the socket named by <see cref="EnvVar"/>. Returns null (never throws) when
    /// the variable is absent — plain terminal mode — or when the connect fails.
    /// </summary>
    public static ShellChannel? TryConnectFromEnvironment(ILogger<ShellChannel> logger)
        => TryConnect(Environment.GetEnvironmentVariable(EnvVar), logger);

    /// <summary>Connects to an explicit socket path; null when the path is empty or unreachable.</summary>
    public static ShellChannel? TryConnect(string? socketPath, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(socketPath))
        {
            return null;
        }

        try
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Connect(new UnixDomainSocketEndPoint(socketPath));
            return new ShellChannel(socket, logger);
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            logger.LogWarning(ex, "Shell channel socket {Path} unreachable; continuing without a shell", socketPath);
            return null;
        }
    }

    /// <inheritdoc />
    public Task<string?> GetCdpEndpointAsync(CancellationToken cancellationToken = default)
    {
        lock (_endpointGate)
        {
            _endpointTask ??= HelloAsync();
            return _endpointTask;
        }

        async Task<string?> HelloAsync()
        {
            var result = await RequestAsync("hello", new { }).ConfigureAwait(false);
            return result?.TryGetProperty("cdpEndpoint", out var ep) == true ? ep.GetString() : null;
        }
    }

    /// <inheritdoc />
    public async Task<bool> SetPaneVisibleAsync(bool visible, CancellationToken cancellationToken = default)
        => await RequestAsync("setPane", new { visible }).ConfigureAwait(false) is not null;

    /// <inheritdoc />
    public async Task<bool> SetModeAsync(string mode, CancellationToken cancellationToken = default)
        => await RequestAsync("setMode", new { mode }).ConfigureAwait(false) is not null;

    /// <inheritdoc />
    public async Task<bool> CreatePageAsync(string tag, CancellationToken cancellationToken = default)
        => await RequestAsync("createPage", new { tag }).ConfigureAwait(false) is not null;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _dead = true;
        await _lifetime.CancelAsync().ConfigureAwait(false);
        FailAllPending();
        try
        {
            await _writer.DisposeAsync().ConfigureAwait(false);
        }
        catch (IOException)
        {
            // Socket already gone — nothing to flush.
        }

        _lifetime.Dispose();
        _writeLock.Dispose();
    }

    private async Task<JsonElement?> RequestAsync(string method, object payload)
    {
        if (_dead)
        {
            return null;
        }

        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            var line = JsonSerializer.Serialize(new ShellRequest(id, method, payload), JsonOptions);
            await _writeLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await _writer.WriteLineAsync(line).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }

            var done = await Task.WhenAny(tcs.Task, Task.Delay(RequestTimeout)).ConfigureAwait(false);
            if (done != tcs.Task)
            {
                _logger.LogWarning("Shell channel request {Method} timed out", method);
                return null;
            }

            return await tcs.Task.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            MarkDead(ex);
            return null;
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task ReadLoopAsync(CancellationToken token)
    {
        using var reader = new StreamReader(_stream, leaveOpen: true);
        try
        {
            while (!token.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(token).ConfigureAwait(false);
                if (line is null)
                {
                    break; // EOF: shell closed the socket.
                }

                if (line.Length == 0)
                {
                    continue;
                }

                Dispatch(line);
            }
        }
        catch (OperationCanceledException)
        {
            // Disposal.
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            MarkDead(ex);
            return;
        }

        MarkDead(exception: null);
    }

    private void Dispatch(string line)
    {
        JsonElement msg;
        try
        {
            msg = JsonSerializer.Deserialize<JsonElement>(line);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Shell channel sent an unparseable line");
            return;
        }

        if (msg.TryGetProperty("id", out var idEl) && idEl.TryGetInt64(out var id))
        {
            if (!_pending.TryRemove(id, out var tcs))
            {
                return;
            }

            var ok = msg.TryGetProperty("ok", out var okEl) && okEl.GetBoolean();
            if (!ok)
            {
                var error = msg.TryGetProperty("error", out var errEl) ? errEl.GetString() : "unknown";
                _logger.LogWarning("Shell channel request failed: {Error}", error);
                tcs.TrySetResult(null);
                return;
            }

            tcs.TrySetResult(msg.TryGetProperty("result", out var result) ? result.Clone() : default(JsonElement));
            return;
        }

        if (msg.TryGetProperty("event", out var evEl) && evEl.GetString() == "mode"
            && msg.TryGetProperty("params", out var p)
            && p.TryGetProperty("mode", out var modeEl)
            && modeEl.GetString() is { } mode)
        {
            ModeChanged?.Invoke(mode);
        }
    }

    private void MarkDead(Exception? exception)
    {
        if (_dead)
        {
            return;
        }

        _dead = true;
        _logger.LogWarning(exception, "Shell channel lost; continuing without a shell");
        FailAllPending();
    }

    private void FailAllPending()
    {
        foreach (var key in _pending.Keys)
        {
            if (_pending.TryRemove(key, out var tcs))
            {
                tcs.TrySetResult(null);
            }
        }
    }

    private sealed record ShellRequest(long Id, string Method, object Params);
}
