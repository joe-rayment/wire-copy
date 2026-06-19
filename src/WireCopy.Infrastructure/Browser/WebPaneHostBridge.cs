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
/// Child-side half of the browser-hosted web pane and the <see cref="IWebPaneSink"/> the orchestrator
/// drives. When launched under the web host (the <c>WIRECOPY_WEBPANE_SOCKET</c> env var points at a
/// Unix-domain socket) this connects to the host, ensures a dedicated display page exists, and — per
/// the mode the render path requests — either streams the REAL page via CDP <c>Page.startScreencast</c>
/// (live/interactive pages and reader-view articles alike, workspace-8a5y) or collapses the pane
/// (nothing live to show). It also applies pointer/keyboard/navigation input the host forwards back.
/// When the env var is absent (a plain terminal run) the connection never opens and the sink is inert.
///
/// Wire framing (see <see cref="PaneFraming"/>): <c>[len][type][payload]</c>.
///   type 1 (FRAME, child→host): JPEG bytes.
///   type 2 (INPUT, host→child): UTF-8 JSON input message.
///   type 3 (CONTROL, child→host): UTF-8 JSON pane control (mode / toggle).
/// </summary>
public sealed class WebPaneHostBridge : IHostedService, IWebPaneSink, IDisposable, IAsyncDisposable
{
    private readonly IBrowserSession _session;
    private readonly ILogger<WebPaneHostBridge> _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _applyLock = new(1, 1);
    private readonly object _stateLock = new();
    private readonly bool _enabled;
    private Task? _runLoop;
    private Socket? _socket;
    private ICDPSession? _cdp;
    private bool _screencastOn;

    // Desired pane state, set by the orchestrator via Update(); applied to the host whenever connected.
    private WebPaneMode _desiredMode = WebPaneMode.Hidden;

    // Last state actually sent to the host. Null forces a (re)apply on connect.
    private WebPaneMode? _sentMode;
    private bool _disposed;

    public WebPaneHostBridge(IBrowserSession session, ILogger<WebPaneHostBridge> logger)
    {
        _session = session;
        _logger = logger;
        _enabled = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WIRECOPY_WEBPANE_SOCKET"));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var socketPath = Environment.GetEnvironmentVariable("WIRECOPY_WEBPANE_SOCKET");
        if (!_enabled || string.IsNullOrWhiteSpace(socketPath))
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

    /// <inheritdoc />
    public void Update(WebPaneMode mode)
    {
        if (!_enabled)
        {
            return;
        }

        lock (_stateLock)
        {
            if (mode == _desiredMode)
            {
                return; // unchanged — nothing to resend.
            }

            _desiredMode = mode;
        }

        _ = Task.Run(() => ApplyDesiredAsync(_cts.Token));
    }

    /// <inheritdoc />
    public void Toggle()
    {
        if (!_enabled || _socket is not { } socket)
        {
            return;
        }

        _ = SendControlAsync(socket, BuildToggleMessage(), _cts.Token);
    }

    // Sync disposal kept for the DI container, which disposes singletons synchronously (mirrors
    // DockSpotlight); the container rejects services that implement only IAsyncDisposable.
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // already torn down
        }

        _cts.Dispose();
        _socket?.Dispose();
        _writeLock.Dispose();
        _applyLock.Dispose();
    }

    /// <summary>The control message the client interprets as "flip pane visibility" (the 'O' key).</summary>
    internal static string BuildToggleMessage() => JsonSerializer.Serialize(new { kind = "toggle" });

    /// <summary>
    /// Builds the control message the client applies for a pane mode: live (show the screencast of the
    /// real page) or hidden (collapse the pane). Extracted so the SPA-facing contract is unit-testable
    /// without a socket or browser.
    /// </summary>
    internal static string BuildModeMessage(WebPaneMode mode) => mode switch
    {
        WebPaneMode.Live => JsonSerializer.Serialize(new { kind = "mode", mode = "live" }),
        _ => JsonSerializer.Serialize(new { kind = "mode", mode = "hidden" }),
    };

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

            _socket = null;
            _sentMode = null; // force a fresh apply on reconnect
            await StopScreencastAsync().ConfigureAwait(false);
            _cdp = null;
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
                    await SendAsync(socket, PaneFraming.TypeFrame, Convert.FromBase64String(data), ct).ConfigureAwait(false);
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

        // Apply whatever the render path most recently requested (defaults to Live until the first
        // Update arrives, so a tab opened mid-session still shows something immediately).
        await ApplyDesiredAsync(ct).ConfigureAwait(false);

        // Read input frames from the host until the connection drops.
        var header = new byte[4];
        while (!ct.IsCancellationRequested)
        {
            if (!await ReadExactAsync(socket, header, ct).ConfigureAwait(false))
            {
                break;
            }

            var len = BinaryPrimitives.ReadInt32BigEndian(header);
            if (len <= 0 || len > PaneFraming.MaxPayload)
            {
                break;
            }

            var buf = new byte[len];
            if (!await ReadExactAsync(socket, buf, ct).ConfigureAwait(false))
            {
                break;
            }

            if (buf[0] == PaneFraming.TypeInput)
            {
                await HandleInputAsync(page, Encoding.UTF8.GetString(buf, 1, len - 1)).ConfigureAwait(false);
            }
        }
    }

    private async Task ApplyDesiredAsync(CancellationToken ct)
    {
        if (_socket is not { } socket || _cdp is null)
        {
            return; // not connected yet; ServeAsync will apply on connect.
        }

        await _applyLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            WebPaneMode mode;
            lock (_stateLock)
            {
                mode = _desiredMode;
            }

            if (_sentMode == mode)
            {
                return;
            }

            switch (mode)
            {
                case WebPaneMode.Live:
                    await StartScreencastAsync().ConfigureAwait(false);
                    await SendControlAsync(socket, BuildModeMessage(WebPaneMode.Live), ct).ConfigureAwait(false);
                    break;

                default: // Hidden — nothing live to show; collapse the pane and stop streaming.
                    await StopScreencastAsync().ConfigureAwait(false);
                    await SendControlAsync(socket, BuildModeMessage(WebPaneMode.Hidden), ct).ConfigureAwait(false);
                    break;
            }

            _sentMode = mode;
        }
        catch (OperationCanceledException)
        {
            // Shutdown or disconnect mid-apply; the next connect/Update re-applies.
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Web pane apply failed for mode {Mode}", _desiredMode);
        }
        finally
        {
            _applyLock.Release();
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

                case "paneVisible":
                    // workspace-jria: the user hid/showed the pane in their tab. Pause the CDP
                    // screencast while hidden so a collapsed pane costs nothing; resume it on un-hide
                    // if the current desired mode still wants the live stream.
                    var visible = root.TryGetProperty("visible", out var vis) && vis.GetBoolean();
                    if (visible)
                    {
                        if (_desiredMode == WebPaneMode.Live)
                        {
                            await StartScreencastAsync().ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        await StopScreencastAsync().ConfigureAwait(false);
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

    private async Task StartScreencastAsync()
    {
        if (_cdp is null || _screencastOn)
        {
            return;
        }

        await _cdp.SendAsync("Page.startScreencast", new Dictionary<string, object>
        {
            ["format"] = "jpeg",
            ["quality"] = 70,
            ["maxWidth"] = 1280,
            ["maxHeight"] = 2400,
            ["everyNthFrame"] = 1,
        }).ConfigureAwait(false);
        _screencastOn = true;
        _logger.LogInformation("Web pane screencast started");
    }

    private async Task StopScreencastAsync()
    {
        if (_cdp is null || !_screencastOn)
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

        _screencastOn = false;
    }

    private Task SendControlAsync(Socket socket, string json, CancellationToken ct)
        => SendAsync(socket, PaneFraming.TypeControl, Encoding.UTF8.GetBytes(json), ct);

    private async Task SendAsync(Socket socket, byte type, byte[] payload, CancellationToken ct)
    {
        var frame = PaneFraming.Encode(type, payload);

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
