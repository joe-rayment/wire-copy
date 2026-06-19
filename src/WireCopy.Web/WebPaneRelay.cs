// Licensed under the MIT License. See LICENSE in the repository root.
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;

namespace WireCopy.Web;

/// <summary>
/// Bridges a tab's web-pane websocket to its <see cref="PaneSession"/>: screencast frames from the
/// WireCopy.API child are pushed to the tab as binary messages (latest-wins, so a slow tab never
/// stalls the stream), control messages (pane mode / toggle) are pushed as text messages (never
/// dropped — a missed mode change would leave the pane wrong), and text input/control messages from
/// the tab are forwarded down to the child. A single send lock serializes all writes to the websocket.
/// </summary>
internal static class WebPaneRelay
{
    public static async Task RunAsync(string sessionId, WebSocket socket, ILogger log, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(sessionId))
        {
            await CloseAsync(socket, "no-session");
            return;
        }

        var pane = await PaneRegistry.WaitForAsync(sessionId, TimeSpan.FromSeconds(30), ct);
        if (pane is null)
        {
            log.LogWarning("Web pane relay: no session {Session}", sessionId);
            await CloseAsync(socket, "no-pane");
            return;
        }

        // Latest-wins frame buffer so a slow consumer drops stale frames instead of stalling.
        var frames = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(2)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

        // Control messages must NOT be dropped — an unbounded queue keeps every mode change.
        var controls = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });

        // WebSocket sends cannot overlap; both sender tasks acquire this before writing.
        var sendLock = new SemaphoreSlim(1, 1);

        void OnFrame(byte[] data) => frames.Writer.TryWrite(data);
        void OnControl(string json) => controls.Writer.TryWrite(json);
        pane.FrameReceived += OnFrame;
        pane.ControlReceived += OnControl;

        var frameSender = Task.Run(async () =>
        {
            try
            {
                await foreach (var frame in frames.Reader.ReadAllAsync(ct))
                {
                    await SendAsync(socket, sendLock, frame, WebSocketMessageType.Binary, ct);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                log.LogDebug(ex, "web pane frame sender ended");
            }
        });

        var controlSender = Task.Run(async () =>
        {
            try
            {
                await foreach (var json in controls.Reader.ReadAllAsync(ct))
                {
                    await SendAsync(socket, sendLock, Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, ct);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                log.LogDebug(ex, "web pane control sender ended");
            }
        });

        var recv = new byte[16384];
        try
        {
            while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(recv.AsMemory(), ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    await pane.SendInputAsync(Encoding.UTF8.GetString(recv, 0, result.Count), ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException)
        {
        }
        finally
        {
            pane.FrameReceived -= OnFrame;
            pane.ControlReceived -= OnControl;
            frames.Writer.TryComplete();
            controls.Writer.TryComplete();
            await Task.WhenAll(frameSender, controlSender);
            await CloseAsync(socket, "bye");
        }
    }

    private static async Task SendAsync(WebSocket socket, SemaphoreSlim sendLock, byte[] data, WebSocketMessageType type, CancellationToken ct)
    {
        if (socket.State != WebSocketState.Open)
        {
            return;
        }

        await sendLock.WaitAsync(ct);
        try
        {
            await socket.SendAsync(data, type, true, ct);
        }
        finally
        {
            sendLock.Release();
        }
    }

    private static async Task CloseAsync(WebSocket socket, string reason)
    {
        try
        {
            if (socket.State == WebSocketState.Open)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, reason, CancellationToken.None);
            }
        }
        catch
        {
            // ignore
        }
    }
}
