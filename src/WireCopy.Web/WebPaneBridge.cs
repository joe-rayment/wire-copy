// Licensed under the MIT License. See LICENSE in the repository root.
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Playwright;

namespace WireCopy.Web;

/// <summary>
/// Streams a live Patchright/Playwright page into the browser tab via CDP
/// <c>Page.startScreencast</c> (JPEG frames pushed as binary websocket messages) and forwards
/// pointer/keyboard input back via <c>Input.dispatch*</c>. This is the spike proof that the web
/// pane can show a real, interactive page inside the single tab — no second OS window, never the
/// rejected headless-and-invisible mode (the user sees and drives the page through the stream).
/// </summary>
internal static class WebPaneBridge
{
    private static readonly SemaphoreSlim BrowserGate = new(1, 1);
    private static IPlaywright? _playwright;
    private static IBrowser? _browser;

    public static async Task RunAsync(WebSocket socket, ILogger log, CancellationToken ct)
    {
        IBrowserContext? context = null;
        try
        {
            var browser = await GetBrowserAsync(log);
            context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                ViewportSize = new ViewportSize { Width = 1024, Height = 900 },
            });
            var page = await context.NewPageAsync();

            var startUrl = Environment.GetEnvironmentVariable("WIRECOPY_WEBPANE_URL")
                ?? "http://127.0.0.1:5099/testpage.html";
            await page.GotoAsync(startUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

            var cdp = await context.NewCDPSessionAsync(page);
            var sendLock = new SemaphoreSlim(1, 1);

            cdp.Event("Page.screencastFrame").OnEvent += async (_, payload) =>
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
                        var bytes = Convert.FromBase64String(data);
                        await sendLock.WaitAsync(ct);
                        try
                        {
                            if (socket.State == WebSocketState.Open)
                            {
                                await socket.SendAsync(bytes, WebSocketMessageType.Binary, true, ct);
                            }
                        }
                        finally
                        {
                            sendLock.Release();
                        }
                    }

                    await cdp.SendAsync("Page.screencastFrameAck", new Dictionary<string, object>
                    {
                        ["sessionId"] = sessionId,
                    });
                }
                catch (Exception ex)
                {
                    log.LogDebug(ex, "screencastFrame relay failed");
                }
            };

            await cdp.SendAsync("Page.startScreencast", new Dictionary<string, object>
            {
                ["format"] = "jpeg",
                ["quality"] = 70,
                ["maxWidth"] = 1024,
                ["maxHeight"] = 2000,
                ["everyNthFrame"] = 1,
            });

            log.LogInformation("Screencast started for {Url}", startUrl);

            // Receive input/control from the tab.
            var recv = new byte[16384];
            while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(recv.AsMemory(), ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    continue;
                }

                await HandleInputAsync(Encoding.UTF8.GetString(recv, 0, result.Count), cdp, page, log, ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Web pane bridge failed");
        }
        finally
        {
            if (context is not null)
            {
                try
                {
                    await context.CloseAsync();
                }
                catch
                {
                    // best effort
                }
            }
        }
    }

    private static async Task HandleInputAsync(string json, ICDPSession cdp, IPage page, ILogger log, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var type = root.GetProperty("type").GetString();
            switch (type)
            {
                case "click":
                {
                    var x = root.GetProperty("x").GetDouble();
                    var y = root.GetProperty("y").GetDouble();
                    await DispatchMouseAsync(cdp, "mousePressed", x, y, ct);
                    await DispatchMouseAsync(cdp, "mouseReleased", x, y, ct);
                    break;
                }

                case "move":
                {
                    var x = root.GetProperty("x").GetDouble();
                    var y = root.GetProperty("y").GetDouble();
                    await DispatchMouseAsync(cdp, "mouseMoved", x, y, ct);
                    break;
                }

                case "key":
                {
                    var text = root.GetProperty("text").GetString() ?? string.Empty;
                    await cdp.SendAsync("Input.dispatchKeyEvent", new Dictionary<string, object>
                    {
                        ["type"] = "keyDown",
                        ["text"] = text,
                    });
                    await cdp.SendAsync("Input.dispatchKeyEvent", new Dictionary<string, object>
                    {
                        ["type"] = "keyUp",
                        ["text"] = text,
                    });
                    break;
                }

                case "navigate":
                {
                    var url = root.GetProperty("url").GetString();
                    if (!string.IsNullOrWhiteSpace(url))
                    {
                        await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
                    }

                    break;
                }
            }
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Bad webpane input message: {Json}", json);
        }
    }

    private static Task DispatchMouseAsync(ICDPSession cdp, string type, double x, double y, CancellationToken ct)
        => cdp.SendAsync("Input.dispatchMouseEvent", new Dictionary<string, object>
        {
            ["type"] = type,
            ["x"] = x,
            ["y"] = y,
            ["button"] = "left",
            ["buttons"] = type == "mousePressed" ? 1 : 0,
            ["clickCount"] = 1,
        });

    private static async Task<IBrowser> GetBrowserAsync(ILogger log)
    {
        if (_browser is { } existing)
        {
            return existing;
        }

        await BrowserGate.WaitAsync();
        try
        {
            if (_browser is { } b)
            {
                return b;
            }

            _playwright = await Playwright.CreateAsync();
            var exe = Environment.GetEnvironmentVariable("WIRECOPY_CHROMIUM");
            if (string.IsNullOrEmpty(exe))
            {
                var candidate = "/opt/pw-browsers/chromium-1194/chrome-linux/chrome";
                if (File.Exists(candidate))
                {
                    exe = candidate;
                }
            }

            var launch = new BrowserTypeLaunchOptions
            {
                Headless = true,
                Args = new[] { "--no-sandbox", "--disable-gpu" },
            };
            if (!string.IsNullOrEmpty(exe))
            {
                launch.ExecutablePath = exe;
                log.LogInformation("Launching Chromium at {Exe}", exe);
            }

            _browser = await _playwright.Chromium.LaunchAsync(launch);
            return _browser;
        }
        finally
        {
            BrowserGate.Release();
        }
    }
}
