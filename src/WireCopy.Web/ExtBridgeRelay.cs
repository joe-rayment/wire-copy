// Licensed under the MIT License. See LICENSE in the repository root.
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;

namespace WireCopy.Web;

/// <summary>
/// Bridges a tab's <c>/ws/ext</c> websocket (the Chrome extension's background service worker) to its
/// <see cref="ExtSession"/> (the WireCopy.API child running in extension mode). JSON commands the
/// orchestrator emits (navigate / requestDom / scrollTo / highlight / click / emulate) are pushed to
/// the extension as text messages; JSON replies + events the extension emits (ready / domSnapshot /
/// navigated / actionResult / userInteraction) are forwarded down to the child. A single send lock
/// serializes websocket writes. See <see cref="ExtSession"/> for the full message protocol.
///
/// <para>Unlike the screencast relay, DOM snapshots can be multi-megabyte and span several websocket
/// frames, so the receive path accumulates fragments until <c>EndOfMessage</c> before forwarding.</para>
/// </summary>
internal static class ExtBridgeRelay
{
    public static async Task RunAsync(string sessionId, WebSocket socket, ILogger log, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(sessionId))
        {
            await CloseAsync(socket, "no-session");
            return;
        }

        var ext = await ExtRegistry.WaitForAsync(sessionId, TimeSpan.FromSeconds(30), ct);
        if (ext is null)
        {
            log.LogWarning("Extension relay: no session {Session}", sessionId);
            await CloseAsync(socket, "no-ext");
            return;
        }

        // Commands from the orchestrator must never be dropped — an unbounded queue keeps each one.
        var outbound = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        var sendLock = new SemaphoreSlim(1, 1);

        void OnChildMessage(string json) => outbound.Writer.TryWrite(json);
        ext.MessageFromChild += OnChildMessage;

        var sender = Task.Run(async () =>
        {
            try
            {
                await foreach (var json in outbound.Reader.ReadAllAsync(ct))
                {
                    await SendAsync(socket, sendLock, Encoding.UTF8.GetBytes(json), ct);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                log.LogDebug(ex, "ext relay sender ended");
            }
        });

        var buffer = new byte[64 * 1024];
        var assembled = new MemoryStream();
        try
        {
            while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                ValueWebSocketReceiveResult result;
                try
                {
                    result = await socket.ReceiveAsync(buffer.AsMemory(), ct);
                }
                catch (WebSocketException)
                {
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                assembled.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage)
                {
                    continue;
                }

                if (result.MessageType == WebSocketMessageType.Text && assembled.Length > 0)
                {
                    var json = Encoding.UTF8.GetString(assembled.GetBuffer(), 0, (int)assembled.Length);
                    await ext.SendToChildAsync(json, ct);
                }

                assembled.SetLength(0);
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
            ext.MessageFromChild -= OnChildMessage;
            outbound.Writer.TryComplete();
            await sender;
            await CloseAsync(socket, "bye");
        }
    }

    private static async Task SendAsync(WebSocket socket, SemaphoreSlim sendLock, byte[] data, CancellationToken ct)
    {
        if (socket.State != WebSocketState.Open)
        {
            return;
        }

        await sendLock.WaitAsync(ct);
        try
        {
            await socket.SendAsync(data, WebSocketMessageType.Text, true, ct);
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
