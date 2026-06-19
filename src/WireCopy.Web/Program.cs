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

// Terminal stream: PTY child <-> websocket <-> xterm.js.
app.Map("/ws/terminal", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    await TerminalBridge.RunAsync(socket, log, context.RequestAborted);
});

// Web pane: CDP screencast of a live page <-> websocket; input forwarded back.
app.Map("/ws/webpane", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    await WebPaneBridge.RunAsync(socket, log, context.RequestAborted);
});

var url = Environment.GetEnvironmentVariable("WIRECOPY_WEB_URL") ?? "http://127.0.0.1:5099";
app.Run(url);
