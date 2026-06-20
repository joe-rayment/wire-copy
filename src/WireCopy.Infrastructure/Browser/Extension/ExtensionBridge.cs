// Licensed under the MIT License. See LICENSE in the repository root.

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WireCopy.Application.Interfaces.Browser;

namespace WireCopy.Infrastructure.Browser.Extension;

/// <summary>
/// Backend (WireCopy.API child) side of the extension control channel (workspace-ozn8, workspace-wrs5).
/// Connects to the per-tab Unix-domain socket the web host provisioned (<c>WIRECOPY_EXT_SOCKET</c>) and
/// exchanges the JSON protocol documented on <c>WireCopy.Web.ExtSession</c>: it sends commands
/// (navigate / requestDom / scrollTo / highlight / click / emulate) correlated by a numeric <c>id</c>
/// and awaits the matching reply (domSnapshot / actionResult), while surfacing unsolicited events
/// (ready / navigated / userInteraction).
///
/// <para>Registered as a hosted service; <see cref="StartAsync"/> is inert unless
/// <c>WIRECOPY_EXT_SOCKET</c> is set, so it never interferes with the native terminal app or the
/// legacy screencast web mode.</para>
///
/// <para>Wire framing matches the host: <c>[4-byte big-endian payload length][1 type byte = 1][UTF-8 JSON]</c>.</para>
/// </summary>
public sealed class ExtensionBridge : IExtensionBridge, IHostedService, IAsyncDisposable
{
    private const byte TypeJson = 1;

    private readonly ILogger<ExtensionBridge> _logger;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();

    // Swapped for a fresh gate on disconnect (workspace-blg5.6) so a command issued during a transient
    // reconnect waits for the NEXT 'ready' rather than seeing the stale (already-completed) one.
    private volatile TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Socket? _socket;
    private Task? _readLoop;
    private int _nextId;
    private volatile bool _connected;
    private volatile string _currentUrl = string.Empty;

    public ExtensionBridge(ILogger<ExtensionBridge> logger)
    {
        _logger = logger;
    }

    public event Action<string>? Navigated;

    public event Action<string>? UserInteraction;

    public bool IsConnected => _connected;

    public string CurrentUrl => _currentUrl;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var path = Environment.GetEnvironmentVariable("WIRECOPY_EXT_SOCKET");
        if (string.IsNullOrEmpty(path))
        {
            _logger.LogInformation("ExtensionBridge: WIRECOPY_EXT_SOCKET unset — inert (not in extension mode)");
            return Task.CompletedTask;
        }

        _readLoop = Task.Run(() => ConnectAndRunAsync(path, _cts.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            _socket?.Close();
        }
        catch
        {
            // best effort
        }
    }

    public async Task<bool> WaitForReadyAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (_connected)
        {
            return true;
        }

        var readyTask = _ready.Task; // volatile snapshot — _ready is swapped on disconnect
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
        var timeoutTask = Task.Delay(timeout, linked.Token);
        var completed = await Task.WhenAny(readyTask, timeoutTask).ConfigureAwait(false);
        return completed == readyTask;
    }

    public async Task<ExtensionDomSnapshot> NavigateAndCaptureAsync(string url, CancellationToken cancellationToken = default)
    {
        var (id, json) = BuildCommand("navigate", w => w.WriteString("url", url));
        var reply = await SendAndAwaitAsync(id, json, TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
        return ReadSnapshot(reply, url);
    }

    public async Task<ExtensionDomSnapshot> CaptureDomAsync(CancellationToken cancellationToken = default)
    {
        var (id, json) = BuildCommand("requestDom", _ => { });
        var reply = await SendAndAwaitAsync(id, json, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        return ReadSnapshot(reply, string.Empty);
    }

    public async Task<bool> ScrollToAsync(string? selector, double? y, CancellationToken cancellationToken = default)
    {
        var (id, json) = BuildCommand("scrollTo", w =>
        {
            if (selector != null)
            {
                w.WriteString("selector", selector);
            }

            if (y.HasValue)
            {
                w.WriteNumber("y", y.Value);
            }
        });
        return await SendActionAsync(id, json, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> HighlightAsync(string? selector, string? url, string? text, CancellationToken cancellationToken = default)
    {
        var (id, json) = BuildCommand("highlight", w =>
        {
            if (selector != null)
            {
                w.WriteString("selector", selector);
            }

            if (url != null)
            {
                w.WriteString("url", url);
            }

            if (text != null)
            {
                w.WriteString("text", text);
            }
        });
        return await SendActionAsync(id, json, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> ClearHighlightAsync(CancellationToken cancellationToken = default)
    {
        var (id, json) = BuildCommand("clearHighlight", _ => { });
        return await SendActionAsync(id, json, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> SetLayoutAsync(string mode, double ratio, CancellationToken cancellationToken = default)
    {
        var (id, json) = BuildCommand("layout", w =>
        {
            w.WriteString("mode", mode);
            w.WriteNumber("ratio", ratio);
        });
        return await SendActionAsync(id, json, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> ClickAsync(string? selector, string? url, string? text, double? x, double? y, CancellationToken cancellationToken = default)
    {
        var (id, json) = BuildCommand("click", w =>
        {
            if (selector != null)
            {
                w.WriteString("selector", selector);
            }

            if (url != null)
            {
                w.WriteString("url", url);
            }

            if (text != null)
            {
                w.WriteString("text", text);
            }

            if (x.HasValue)
            {
                w.WriteNumber("x", x.Value);
            }

            if (y.HasValue)
            {
                w.WriteNumber("y", y.Value);
            }
        });
        return await SendActionAsync(id, json, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> EmulateAsync(bool mobile, int? width, CancellationToken cancellationToken = default)
    {
        var (id, json) = BuildCommand("emulate", w =>
        {
            w.WriteBoolean("mobile", mobile);
            if (width.HasValue)
            {
                w.WriteNumber("width", width.Value);
            }
        });
        return await SendActionAsync(id, json, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            _socket?.Close();
        }
        catch
        {
            // best effort
        }

        if (_readLoop != null)
        {
            try
            {
                await _readLoop.ConfigureAwait(false);
            }
            catch
            {
                // best effort
            }
        }

        _cts.Dispose();
    }

    /// <summary>
    /// Test seam: connect to an explicit control-socket path without going through the
    /// <c>WIRECOPY_EXT_SOCKET</c> environment variable (which would race across parallel tests).
    /// </summary>
    internal void ConnectForTest(string socketPath)
    {
        _readLoop = Task.Run(() => ConnectAndRunAsync(socketPath, _cts.Token));
    }

    private static async Task<bool> ReadExactAsync(Socket socket, byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await socket.ReceiveAsync(buffer.AsMemory(read), SocketFlags.None, ct).ConfigureAwait(false);
            if (n == 0)
            {
                return false;
            }

            read += n;
        }

        return true;
    }

    private static ExtensionDomSnapshot ReadSnapshot(JsonElement reply, string fallbackUrl)
    {
        var url = reply.TryGetProperty("url", out var u) ? u.GetString() ?? fallbackUrl : fallbackUrl;
        var html = reply.TryGetProperty("html", out var h) ? h.GetString() ?? string.Empty : string.Empty;
        var w = 0;
        var ht = 0;
        if (reply.TryGetProperty("viewport", out var vp) && vp.ValueKind == JsonValueKind.Object)
        {
            if (vp.TryGetProperty("w", out var vw))
            {
                vw.TryGetInt32(out w);
            }

            if (vp.TryGetProperty("h", out var vh))
            {
                vh.TryGetInt32(out ht);
            }
        }

        return new ExtensionDomSnapshot(url, html, w, ht);
    }

    private (int Id, string Json) BuildCommand(string type, Action<Utf8JsonWriter> writeExtra)
    {
        var id = Interlocked.Increment(ref _nextId);
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            writer.WriteString("type", type);
            writer.WriteNumber("id", id);
            writeExtra(writer);
            writer.WriteEndObject();
        }

        return (id, Encoding.UTF8.GetString(ms.ToArray()));
    }

    private async Task<bool> SendActionAsync(int id, string json, CancellationToken cancellationToken)
    {
        var reply = await SendAndAwaitAsync(id, json, TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
        return !reply.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.False;
    }

    private async Task<JsonElement> SendAndAwaitAsync(int id, string json, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!_connected)
        {
            // Transient reconnect grace (workspace-blg5.6): wait briefly for the extension to (re)connect
            // and report 'ready' instead of failing the command outright during a reconnect window.
            var ready = await WaitForReadyAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            if (!ready)
            {
                throw new InvalidOperationException("Extension is not connected.");
            }
        }

        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            await SendRawAsync(json, cancellationToken).ConfigureAwait(false);

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
            linked.CancelAfter(timeout);
            await using var reg = linked.Token.Register(() =>
                tcs.TrySetException(new TimeoutException($"Extension did not reply to command id={id} within {timeout.TotalSeconds:0}s.")));
            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task SendRawAsync(string json, CancellationToken cancellationToken)
    {
        var socket = _socket ?? throw new InvalidOperationException("Extension socket not connected.");
        var payload = Encoding.UTF8.GetBytes(json);
        var frame = new byte[4 + 1 + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame, payload.Length + 1);
        frame[4] = TypeJson;
        payload.CopyTo(frame, 5);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(frame, SocketFlags.None, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ConnectAndRunAsync(string path, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), ct).ConfigureAwait(false);
                _socket = socket;
                _logger.LogInformation("ExtensionBridge connected to control socket {Path}", path);
                await ReadLoopAsync(socket, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "ExtensionBridge connection ended; retrying");
            }

            // Re-arm the readiness gate BEFORE flipping _connected (workspace-blg5.6) so WaitForReadyAsync
            // never observes _connected=false alongside a stale, already-completed 'ready'. A command
            // issued during the reconnect window then waits for the next 'ready' instead of failing.
            if (!ct.IsCancellationRequested && _ready.Task.IsCompleted)
            {
                _ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            _connected = false;
            FailAllPending(new InvalidOperationException("Extension disconnected."));

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ReadLoopAsync(Socket socket, CancellationToken ct)
    {
        var header = new byte[4];
        while (!ct.IsCancellationRequested)
        {
            if (!await ReadExactAsync(socket, header, ct).ConfigureAwait(false))
            {
                break;
            }

            var len = BinaryPrimitives.ReadInt32BigEndian(header);
            if (len <= 1 || len > 64 * 1024 * 1024)
            {
                break;
            }

            var buf = new byte[len];
            if (!await ReadExactAsync(socket, buf, ct).ConfigureAwait(false))
            {
                break;
            }

            if (buf[0] != TypeJson)
            {
                continue;
            }

            var json = Encoding.UTF8.GetString(buf, 1, len - 1);
            Dispatch(json);
        }
    }

    private void Dispatch(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;

            if (root.TryGetProperty("url", out var urlEl) && urlEl.ValueKind == JsonValueKind.String)
            {
                var u = urlEl.GetString();
                if (!string.IsNullOrEmpty(u))
                {
                    _currentUrl = u;
                }
            }

            switch (type)
            {
                case "ready":
                    _connected = true;
                    _ready.TrySetResult();
                    break;
                case "domSnapshot":
                case "actionResult":
                    if (root.TryGetProperty("id", out var idEl) && idEl.TryGetInt32(out var id)
                        && _pending.TryGetValue(id, out var tcs))
                    {
                        // Clone so the JsonElement survives past the JsonDocument's lifetime.
                        tcs.TrySetResult(root.Clone());
                    }

                    break;
                case "navigated":
                    if (root.TryGetProperty("url", out var nu))
                    {
                        Navigated?.Invoke(nu.GetString() ?? string.Empty);
                    }

                    break;
                case "userInteraction":
                    UserInteraction?.Invoke(root.TryGetProperty("kind", out var k) ? k.GetString() ?? "interaction" : "interaction");
                    break;
                default:
                    _logger.LogDebug("ExtensionBridge: unknown message type {Type}", type);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ExtensionBridge: bad message {Json}", json.Length > 200 ? json[..200] : json);
        }
    }

    private void FailAllPending(Exception ex)
    {
        foreach (var kvp in _pending)
        {
            kvp.Value.TrySetException(ex);
        }

        _pending.Clear();
    }
}
