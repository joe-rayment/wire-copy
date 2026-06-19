// Licensed under the MIT License. See LICENSE in the repository root.
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace WireCopy.Infrastructure.Browser;

/// <summary>
/// Child-side half of the browser-hosted web pane. When launched under the web host
/// (the <c>WIRECOPY_WEBPANE_SOCKET</c> env var points at a Unix-domain socket), this connects to
/// the host, ensures a dedicated display page exists, streams it via CDP <c>Page.startScreencast</c>
/// (JPEG frames pushed to the host as binary), and applies pointer/keyboard/navigation input the
/// host forwards back. When the env var is absent (a plain terminal run) the service is inert.
///
/// Wire framing (both directions): [4-byte big-endian payload length][1 type byte][payload].
///   type 1 (FRAME, child→host): JPEG bytes.
///   type 2 (INPUT, host→child): UTF-8 JSON control/input message.
/// </summary>
public sealed class WebPaneHostBridge : IHostedService, IAsyncDisposable
{
    private const byte TypeFrame = 1;
    private const byte TypeInput = 2;

    private readonly IBrowserSession _session;
    private readonly ILogger<WebPaneHostBridge> _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private Task? _runLoop;
    private Socket? _socket;
    private ICDPSession? _cdp;

    public WebPaneHostBridge(IBrowserSession session, ILogger<WebPaneHostBridge> logger)
    {
        _session = session;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var socketPath = Environment.GetEnvironmentVariable("WIRECOPY_WEBPANE_SOCKET");
        if (string.IsNullOrWhiteSpace(socketPath))
        {
            return Task.CompletedTask; // plain terminal run — nothing to stream.
        }

        _runLoop = Task.Run(() => RunAsync(socketPath, _cts.Token));
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

        if (_runLoop is not null)
        {
            try
            {
                await _runLoop.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // best effort
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        _cts.Dispose();
        _socket?.Dispose();
    }

    private async Task RunAsync(string socketPath, CancellationToken ct)
    {
        // Reconnect loop: the host may not be listening yet, and a tab can reconnect.
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct).ConfigureAwait(false);
                _socket = socket;
                _logger.LogInformation("Web pane bridge connected to host at {Path}", socketPath);
                await ServeAsync(socket, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Web pane bridge connection ended; retrying");
            }

            await StopScreencastAsync().ConfigureAwait(false);
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

    private async Task ServeAsync(Socket socket, CancellationToken ct)
    {
        var page = await _session.GetDisplayPageAsync().ConfigureAwait(false);
        if (page is null)
        {
            _logger.LogWarning("Web pane bridge: no display page available");
            return;
        }

        _cdp = await page.Context.NewCDPSessionAsync(page).ConfigureAwait(false);

        _cdp.Event("Page.screencastFrame").OnEvent += async (_, payload) =>
        {
            if (payload is not { } frame)
            {
                return;
            }

            try
            {
                var data = frame.GetProperty("data").GetString();
                var sessionId = frame.GetProperty("sessionId").GetInt32();
                if (!string.IsNullOrEmpty(data))
                {
                    await SendAsync(socket, TypeFrame, Convert.FromBase64String(data), ct).ConfigureAwait(false);
                }

                await _cdp.SendAsync("Page.screencastFrameAck", new Dictionary<string, object>
                {
                    ["sessionId"] = sessionId,
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "screencast frame relay failed");
            }
        };

        await _cdp.SendAsync("Page.startScreencast", new Dictionary<string, object>
        {
            ["format"] = "jpeg",
            ["quality"] = 70,
            ["maxWidth"] = 1280,
            ["maxHeight"] = 2400,
            ["everyNthFrame"] = 1,
        }).ConfigureAwait(false);

        _logger.LogInformation("Web pane screencast started");

        // Read input/control frames from the host until the connection drops.
        var header = new byte[4];
        while (!ct.IsCancellationRequested)
        {
            if (!await ReadExactAsync(socket, header, ct).ConfigureAwait(false))
            {
                break;
            }

            var len = BinaryPrimitives.ReadInt32BigEndian(header);
            if (len <= 0 || len > 8 * 1024 * 1024)
            {
                break;
            }

            var buf = new byte[len];
            if (!await ReadExactAsync(socket, buf, ct).ConfigureAwait(false))
            {
                break;
            }

            var type = buf[0];
            if (type == TypeInput)
            {
                await HandleInputAsync(page, Encoding.UTF8.GetString(buf, 1, len - 1)).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleInputAsync(IPage page, string json)
    {
        if (_cdp is null)
        {
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            switch (root.GetProperty("type").GetString())
            {
                case "click":
                    await DispatchMouseAsync("mousePressed", root).ConfigureAwait(false);
                    await DispatchMouseAsync("mouseReleased", root).ConfigureAwait(false);
                    break;

                case "move":
                    await DispatchMouseAsync("mouseMoved", root).ConfigureAwait(false);
                    break;

                case "wheel":
                    await _cdp.SendAsync("Input.dispatchMouseEvent", new Dictionary<string, object>
                    {
                        ["type"] = "mouseWheel",
                        ["x"] = root.GetProperty("x").GetDouble(),
                        ["y"] = root.GetProperty("y").GetDouble(),
                        ["deltaX"] = root.TryGetProperty("deltaX", out var dx) ? dx.GetDouble() : 0,
                        ["deltaY"] = root.TryGetProperty("deltaY", out var dy) ? dy.GetDouble() : 0,
                    }).ConfigureAwait(false);
                    break;

                case "key":
                    var text = root.TryGetProperty("text", out var t) ? t.GetString() ?? string.Empty : string.Empty;
                    await _cdp.SendAsync("Input.dispatchKeyEvent", new Dictionary<string, object>
                    {
                        ["type"] = "keyDown",
                        ["text"] = text,
                    }).ConfigureAwait(false);
                    await _cdp.SendAsync("Input.dispatchKeyEvent", new Dictionary<string, object>
                    {
                        ["type"] = "keyUp",
                        ["text"] = text,
                    }).ConfigureAwait(false);
                    break;

                case "navigate":
                    var url = root.GetProperty("url").GetString();
                    if (!string.IsNullOrWhiteSpace(url))
                    {
                        await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded }).ConfigureAwait(false);
                    }

                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "web pane input failed: {Json}", json);
        }
    }

    private Task DispatchMouseAsync(string type, JsonElement root)
        => _cdp!.SendAsync("Input.dispatchMouseEvent", new Dictionary<string, object>
        {
            ["type"] = type,
            ["x"] = root.GetProperty("x").GetDouble(),
            ["y"] = root.GetProperty("y").GetDouble(),
            ["button"] = "left",
            ["buttons"] = type == "mousePressed" ? 1 : 0,
            ["clickCount"] = 1,
        });

    private async Task StopScreencastAsync()
    {
        if (_cdp is null)
        {
            return;
        }

        try
        {
            await _cdp.SendAsync("Page.stopScreencast").ConfigureAwait(false);
        }
        catch
        {
            // best effort — the page/session may already be gone.
        }

        _cdp = null;
    }

    private async Task SendAsync(Socket socket, byte type, byte[] payload, CancellationToken ct)
    {
        var frame = new byte[4 + 1 + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame, payload.Length + 1);
        frame[4] = type;
        payload.CopyTo(frame, 5);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(frame, SocketFlags.None, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

#pragma warning disable SA1204 // static read helper kept at the end with the other I/O helpers
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
