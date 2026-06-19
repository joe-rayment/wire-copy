// Licensed under the MIT License. See LICENSE in the repository root.
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;

namespace WireCopy.Web;

/// <summary>
/// Bridges a tab's web-pane websocket to its <see cref="PaneSession"/>: screencast frames from the
/// WireCopy.API child are pushed to the tab as binary messages (latest-wins, so a slow tab never
/// stalls the stream), and text input/control messages from the tab are forwarded down to the child.
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

        void OnFrame(byte[] data) => frames.Writer.TryWrite(data);
        pane.FrameReceived += OnFrame;

        var sender = Task.Run(async () =>
        {
            try
            {
                await foreach (var frame in frames.Reader.ReadAllAsync(ct))
                {
                    if (socket.State != WebSocketState.Open)
                    {
                        break;
                    }

                    await socket.SendAsync(frame, WebSocketMessageType.Binary, true, ct);
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
            frames.Writer.TryComplete();
            await sender;
            await CloseAsync(socket, "bye");
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
