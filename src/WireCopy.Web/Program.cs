// Licensed under the MIT License. See LICENSE in the repository root.
//
// WireCopy.Web — G0 spike host. Proves the two load-bearing assumptions of the
// browser-hosted ("Cloud Shell") architecture:
//   1. an interactive TTY app runs under a real PTY and renders in xterm.js over a websocket;
//   2. a Playwright/Patchright page is streamed into the tab via CDP Page.startScreencast,
//      with input forwarded back via Input.dispatch* — one browser tab, no second OS window.
using WireCopy.Web;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss ";
});

var app = builder.Build();
var log = app.Logger;

app.UseWebSockets();
app.UseDefaultFiles();
app.UseStaticFiles();

// Terminal stream: PTY child <-> websocket <-> xterm.js. Each tab carries a session id so its
// web pane can be correlated to the same spawned WireCopy.API child.
app.Map("/ws/terminal", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    var sessionId = context.Request.Query["session"].ToString();
    if (string.IsNullOrEmpty(sessionId))
    {
        sessionId = Guid.NewGuid().ToString("n");
    }

    var pane = PaneRegistry.Create(sessionId, log);
    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    try
    {
        await TerminalBridge.RunAsync(socket, log, context.RequestAborted, pane.SocketPath);
    }
    finally
    {
        await PaneRegistry.Remove(sessionId);
    }
});

// Web pane: relays the matching child's CDP screencast frames to the tab and forwards input back.
app.Map("/ws/webpane", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    var sessionId = context.Request.Query["session"].ToString();
    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    await WebPaneRelay.RunAsync(sessionId, socket, log, context.RequestAborted);
});

var url = Environment.GetEnvironmentVariable("WIRECOPY_WEB_URL") ?? "http://127.0.0.1:5099";
app.Run(url);
