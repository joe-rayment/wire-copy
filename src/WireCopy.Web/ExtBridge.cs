// Licensed under the MIT License. See LICENSE in the repository root.
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;

namespace WireCopy.Web;

/// <summary>
/// Host-side half of the backend &lt;-&gt; extension control channel (workspace-ozn8). One
/// <see cref="ExtSession"/> exists per browser tab (correlated by the same session id the SPA/extension
/// passes to <c>/ws/terminal</c> and <c>/ws/ext</c>). It owns a Unix-domain socket the spawned
/// WireCopy.API child connects to when launched in extension mode (<c>WIRECOPY_BROWSER=extension</c>);
/// JSON control messages relay symmetrically between that child (the orchestrator) and the Chrome
/// extension over the <c>/ws/ext</c> websocket (<see cref="ExtBridgeRelay"/>).
///
/// <para>This replaces the screencast pane channel for extension mode: instead of streaming JPEG
/// frames out and input back, it carries a typed JSON protocol both ways.</para>
///
/// <para><b>Wire framing</b> (identical envelope to the pane IPC so the two can share temp-socket
/// plumbing): <c>[4-byte big-endian payload length][1 type byte][payload]</c>; the only type used
/// here is <see cref="TypeJson"/> = 1 (a UTF-8 JSON message).</para>
///
/// <para><b>Message protocol</b> (the JSON payloads; documented here as the single source of truth
/// shared with <c>extension/background.js</c> + <c>extension/content.js</c>):</para>
/// <list type="bullet">
///   <item><b>backend → ext</b> (commands; each carries a numeric <c>id</c> for correlation):
///     <c>{type:"navigate", id, url}</c>,
///     <c>{type:"requestDom", id}</c>,
///     <c>{type:"scrollTo", id, selector?, y?}</c>,
///     <c>{type:"highlight", id, selector?, url?, text?}</c>,
///     <c>{type:"clearHighlight", id}</c>,
///     <c>{type:"click", id, selector?, x?, y?}</c>,
///     <c>{type:"emulate", id, mobile, width?}</c>.</item>
///   <item><b>ext → backend</b>:
///     <c>{type:"ready", url, viewport:{w,h}}</c> (sent on connect / page load),
///     <c>{type:"domSnapshot", id?, url, html, viewport:{w,h}}</c> (reply to requestDom/navigate),
///     <c>{type:"navigated", url}</c> (top-level or SPA route change),
///     <c>{type:"actionResult", id, ok, error?}</c> (reply to scrollTo/highlight/click/emulate),
///     <c>{type:"userInteraction", kind, url?}</c> (user clicked/typed on the underlying page).</item>
/// </list>
/// </summary>
internal sealed class ExtSession : IAsyncDisposable
{
    private const byte TypeJson = 1;

    private readonly Socket _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ILogger _logger;
    private readonly ConcurrentQueue<string> _pendingToChild = new();
    private Socket? _child;

    private ExtSession(string socketPath, Socket listener, ILogger logger)
    {
        SocketPath = socketPath;
        _listener = listener;
        _logger = logger;
    }

    /// <summary>Raised (off the accept loop) for each JSON message the API child emits (a command
    /// destined for the extension). The relay forwards it to the <c>/ws/ext</c> websocket.</summary>
    public event Action<string>? MessageFromChild;

    /// <summary>True once the WireCopy.API child has connected to the control socket.</summary>
    public bool ChildConnected => _child is not null;

    public string SocketPath { get; }

    public static ExtSession Create(string sessionId, ILogger logger)
    {
        var safe = string.Concat(sessionId.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_'));
        if (safe.Length == 0)
        {
            safe = Guid.NewGuid().ToString("n");
        }

        var path = Path.Combine(Path.GetTempPath(), $"wirecopy-ext-{safe}.sock");
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // best effort
        }

        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        listener.Listen(1);

        var session = new ExtSession(path, listener, logger);
        _ = session.AcceptLoopAsync();
        return session;
    }

    /// <summary>Sends a JSON message down to the API child (a response/event from the extension).</summary>
    public async Task SendToChildAsync(string json, CancellationToken ct)
    {
        var child = _child;
        if (child is null)
        {
            // The extension can connect (and announce `ready`) before the WireCopy.API child has
            // attached to the control socket. Buffer so that one-shot startup messages are not lost;
            // the accept loop flushes the queue the moment the child connects.
            _pendingToChild.Enqueue(json);
            return;
        }

        await SendFramedAsync(child, json, ct).ConfigureAwait(false);
    }

    private async Task SendFramedAsync(Socket child, string json, CancellationToken ct)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var frame = new byte[4 + 1 + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame, payload.Length + 1);
        frame[4] = TypeJson;
        payload.CopyTo(frame, 5);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await child.SendAsync(frame, SocketFlags.None, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ext bridge send-to-child failed");
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            _listener.Close();
            _child?.Close();
        }
        catch
        {
            // best effort
        }

        try
        {
            if (File.Exists(SocketPath))
            {
                File.Delete(SocketPath);
            }
        }
        catch
        {
            // best effort
        }

        _cts.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            Socket child;
            try
            {
                child = await _listener.AcceptAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "ext bridge accept ended");
                break;
            }

            _child = child;
            _logger.LogInformation("Extension control child connected on {Path}", SocketPath);

            // Flush anything the extension sent before the child attached (e.g. the initial `ready`).
            while (_pendingToChild.TryDequeue(out var buffered))
            {
                try
                {
                    await SendFramedAsync(child, buffered, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "ext bridge flush-to-child failed");
                }
            }

            try
            {
                await ReadLoopAsync(child, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "ext bridge read loop ended");
            }
            finally
            {
                try
                {
                    child.Close();
                }
                catch
                {
                    // best effort
                }

                _child = null;
            }
        }
    }

    private async Task ReadLoopAsync(Socket child, CancellationToken ct)
    {
        var header = new byte[4];
        while (!ct.IsCancellationRequested)
        {
            if (!await ReadExactAsync(child, header, ct).ConfigureAwait(false))
            {
                break;
            }

            var len = BinaryPrimitives.ReadInt32BigEndian(header);
            if (len <= 0 || len > 64 * 1024 * 1024)
            {
                break;
            }

            var buf = new byte[len];
            if (!await ReadExactAsync(child, buf, ct).ConfigureAwait(false))
            {
                break;
            }

            if (buf[0] == TypeJson)
            {
                MessageFromChild?.Invoke(Encoding.UTF8.GetString(buf, 1, len - 1));
            }
        }
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
}

/// <summary>Process-wide registry mapping a tab's session id to its <see cref="ExtSession"/>.</summary>
internal static class ExtRegistry
{
    private static readonly ConcurrentDictionary<string, ExtSession> Sessions = new();

    public static ExtSession Create(string sessionId, ILogger logger)
    {
        Remove(sessionId).GetAwaiter().GetResult();
        var session = ExtSession.Create(sessionId, logger);
        Sessions[sessionId] = session;
        return session;
    }

    public static async Task<ExtSession?> WaitForAsync(string sessionId, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            if (Sessions.TryGetValue(sessionId, out var s))
            {
                return s;
            }

            await Task.Delay(100, ct).ConfigureAwait(false);
        }

        return Sessions.TryGetValue(sessionId, out var found) ? found : null;
    }

    public static async Task Remove(string sessionId)
    {
        if (Sessions.TryRemove(sessionId, out var s))
        {
            await s.DisposeAsync().ConfigureAwait(false);
        }
    }
}
