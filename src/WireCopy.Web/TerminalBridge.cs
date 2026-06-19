// Licensed under the MIT License. See LICENSE in the repository root.
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Porta.Pty;

namespace WireCopy.Web;

/// <summary>
/// Bridges a real PTY child process to a browser xterm.js terminal over a websocket.
/// Binary websocket frames are raw keystroke bytes written to the PTY; text frames are
/// JSON control messages (currently <c>{"type":"resize","cols":N,"rows":M}</c>).
/// PTY output bytes are streamed back as binary frames. This is the ttyd/Cloud-Shell
/// pattern: the unmodified console TUI sees a genuine terminal, so Console.ReadKey,
/// Console.WindowWidth, cursor positioning and mouse/bracketed-paste all work natively.
/// </summary>
internal static class TerminalBridge
{
    public static async Task RunAsync(WebSocket socket, ILogger log, CancellationToken ct)
    {
        // The command to host. Defaults to bash for the isolated spike; the real host
        // sets this to the WireCopy.API browse process.
        var app = Environment.GetEnvironmentVariable("WIRECOPY_TERMINAL_APP") ?? "/bin/bash";
        var argsRaw = Environment.GetEnvironmentVariable("WIRECOPY_TERMINAL_ARGS");
        var cmdline = string.IsNullOrWhiteSpace(argsRaw)
            ? Array.Empty<string>()
            : argsRaw.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var env = new Dictionary<string, string>
        {
            ["TERM"] = "xterm-256color",
            ["COLORTERM"] = "truecolor",
            ["LANG"] = Environment.GetEnvironmentVariable("LANG") ?? "C.UTF-8",
        };
        foreach (var key in new[] { "PATH", "HOME", "USER", "PLAYWRIGHT_BROWSERS_PATH", "DOTNET_ROOT" })
        {
            var v = Environment.GetEnvironmentVariable(key);
            if (v is not null)
            {
                env[key] = v;
            }
        }

        var options = new PtyOptions
        {
            Name = "xterm-256color",
            Cols = 120,
            Rows = 32,
            Cwd = Environment.GetEnvironmentVariable("WIRECOPY_TERMINAL_CWD") ?? Directory.GetCurrentDirectory(),
            App = app,
            CommandLine = cmdline,
            Environment = env,
        };

        IPtyConnection pty;
        try
        {
            pty = await PtyProvider.SpawnAsync(options, ct);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to spawn PTY for {App}", app);
            await TryCloseAsync(socket, "pty-spawn-failed");
            return;
        }

        log.LogInformation("PTY spawned: {App} pid={Pid}", app, pty.Pid);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // PTY -> websocket
        var pump = Task.Run(async () =>
        {
            var buffer = new byte[8192];
            try
            {
                while (!linked.IsCancellationRequested)
                {
                    var n = await pty.ReaderStream.ReadAsync(buffer.AsMemory(), linked.Token);
                    if (n <= 0)
                    {
                        break;
                    }

                    await socket.SendAsync(buffer.AsMemory(0, n), WebSocketMessageType.Binary, true, linked.Token);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                log.LogDebug(ex, "PTY read pump ended");
            }
            finally
            {
                linked.Cancel();
            }
        });

        // websocket -> PTY
        var recv = new byte[8192];
        try
        {
            while (!linked.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(recv.AsMemory(), linked.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    HandleControl(Encoding.UTF8.GetString(recv, 0, result.Count), pty, log);
                    continue;
                }

                await pty.WriterStream.WriteAsync(recv.AsMemory(0, result.Count), linked.Token);
                await pty.WriterStream.FlushAsync(linked.Token);
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
            linked.Cancel();
            try
            {
                pty.Kill();
            }
            catch
            {
                // best effort
            }

            await pump;
            await TryCloseAsync(socket, "bye");
        }
    }

    private static void HandleControl(string json, IPtyConnection pty, ILogger log)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("type", out var t) && t.GetString() == "resize")
            {
                var cols = root.GetProperty("cols").GetInt32();
                var rows = root.GetProperty("rows").GetInt32();
                if (cols > 0 && rows > 0)
                {
                    pty.Resize(cols, rows);
                }
            }
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Bad terminal control message: {Json}", json);
        }
    }

    private static async Task TryCloseAsync(WebSocket socket, string reason)
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
