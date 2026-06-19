// Licensed under the MIT License. See LICENSE in the repository root.
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;

namespace WireCopy.Web;

/// <summary>
/// Host-side half of the browser-hosted web pane IPC. One <see cref="PaneSession"/> exists per
/// browser tab (correlated by a session id the SPA passes to both websockets). It owns a
/// Unix-domain socket the spawned WireCopy.API child connects to; screencast frames arrive from the
/// child and are surfaced via <see cref="FrameReceived"/>, while pane input is forwarded down with
/// <see cref="SendInputAsync"/>. Framing matches WebPaneHostBridge:
/// [4-byte big-endian payload length][1 type byte][payload]; type 1 = FRAME, type 2 = INPUT.
/// </summary>
internal sealed class PaneSession : IAsyncDisposable
{
    private const byte TypeFrame = 1;
    private const byte TypeInput = 2;
    private const byte TypeControl = 3;

    private readonly Socket _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ILogger _logger;
    private Socket? _child;

    private PaneSession(string socketPath, Socket listener, ILogger logger)
    {
        SocketPath = socketPath;
        _listener = listener;
        _logger = logger;
    }

    /// <summary>Raised (off the accept loop) for each JPEG screencast frame from the child.</summary>
    public event Action<byte[]>? FrameReceived;

    /// <summary>Raised (off the accept loop) for each control message (pane mode / toggle) from the child.</summary>
    public event Action<string>? ControlReceived;

    public string SocketPath { get; }

    public static PaneSession Create(string sessionId, ILogger logger)
    {
        var safe = string.Concat(sessionId.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_'));
        if (safe.Length == 0)
        {
            safe = Guid.NewGuid().ToString("n");
        }

        var path = Path.Combine(Path.GetTempPath(), $"wirecopy-pane-{safe}.sock");
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

        var session = new PaneSession(path, listener, logger);
        _ = session.AcceptLoopAsync();
        return session;
    }

    public async Task SendInputAsync(string json, CancellationToken ct)
    {
        var child = _child;
        if (child is null)
        {
            return;
        }

        var payload = Encoding.UTF8.GetBytes(json);
        var frame = new byte[4 + 1 + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame, payload.Length + 1);
        frame[4] = TypeInput;
        payload.CopyTo(frame, 5);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await child.SendAsync(frame, SocketFlags.None, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "pane input send failed");
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
                _logger.LogDebug(ex, "pane accept ended");
                break;
            }

            _child = child;
            _logger.LogInformation("Web pane child connected on {Path}", SocketPath);
            try
            {
                await ReadLoopAsync(child, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "pane read loop ended");
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
            if (len <= 0 || len > 16 * 1024 * 1024)
            {
                break;
            }

            var buf = new byte[len];
            if (!await ReadExactAsync(child, buf, ct).ConfigureAwait(false))
            {
                break;
            }

            if (buf[0] == TypeFrame)
            {
                var payload = new byte[len - 1];
                Array.Copy(buf, 1, payload, 0, len - 1);
                FrameReceived?.Invoke(payload);
            }
            else if (buf[0] == TypeControl)
            {
                ControlReceived?.Invoke(Encoding.UTF8.GetString(buf, 1, len - 1));
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

/// <summary>Process-wide registry mapping a tab's session id to its <see cref="PaneSession"/>.</summary>
internal static class PaneRegistry
{
    private static readonly ConcurrentDictionary<string, PaneSession> Sessions = new();

    public static PaneSession Create(string sessionId, ILogger logger)
    {
        Remove(sessionId).GetAwaiter().GetResult();
        var session = PaneSession.Create(sessionId, logger);
        Sessions[sessionId] = session;
        return session;
    }

    public static async Task<PaneSession?> WaitForAsync(string sessionId, TimeSpan timeout, CancellationToken ct)
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
