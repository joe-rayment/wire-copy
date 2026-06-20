// Licensed under the MIT License. See LICENSE in the repository root.
//
// WireCopy.Web — G0 spike host. Proves the two load-bearing assumptions of the
// browser-hosted ("Cloud Shell") architecture:
//   1. an interactive TTY app runs under a real PTY and renders in xterm.js over a websocket;
//   2. a Playwright/Patchright page is streamed into the tab via CDP Page.startScreencast,
//      with input forwarded back via Input.dispatch* — one browser tab, no second OS window.
using System.Net.WebSockets;
using WireCopy.Web;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss ";
});

var app = builder.Build();
var log = app.Logger;

// workspace-blg5/P2.3: in extension mode the user's own browser is the renderer and the UI is the
// extension overlay in their own tab. The legacy split-pane shell (index.html) is the WRONG page here —
// its server-side screencast pane is retired, so served as the default document it would sit blank-and-
// open (workspace-yqt5.2). Don't serve it as the default; show a tiny placeholder at "/" instead.
var extensionMode = string.Equals(
    Environment.GetEnvironmentVariable("WIRECOPY_BROWSER"), "extension", StringComparison.OrdinalIgnoreCase);

app.UseWebSockets();
if (!extensionMode)
{
    app.UseDefaultFiles();
}

app.UseStaticFiles();

if (extensionMode)
{
    const string placeholder =
        "<!doctype html><meta charset=\"utf-8\"><title>Wire Copy — extension mode</title>" +
        "<body style=\"margin:0;height:100vh;display:flex;align-items:center;justify-content:center;" +
        "background:#0b0b0e;color:#ddd;font-family:ui-monospace,monospace;text-align:center\">" +
        "<div><h1 style=\"color:#ff5fa2;margin:0 0 .5rem\">Wire Copy</h1>" +
        "<p>Running in <b>extension mode</b> — the interface is the overlay in your own browser tab.</p>" +
        "<p>Load the unpacked extension, then just browse. This page is intentionally blank.</p></div>";
    app.MapGet("/", () => Results.Content(placeholder, "text/html; charset=utf-8"));
}

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

    // Extension mode (workspace-blg5/P2.3): the user's own browser is the renderer, driven over the
    // /ws/ext control channel — there is NO server-side screencast. Skip provisioning the screencast
    // pane socket entirely so the child never spins up the WebPaneHostBridge (which would launch a
    // server-side Chromium). Legacy mode keeps the pane socket for the screencast.
    var pane = extensionMode ? null : PaneRegistry.Create(sessionId, log);
    var ext = ExtRegistry.Create(sessionId, log);

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    try
    {
        await TerminalBridge.RunAsync(socket, log, context.RequestAborted, pane?.SocketPath, sessionId, ext.SocketPath);
    }
    finally
    {
        if (pane is not null)
        {
            await PaneRegistry.Remove(sessionId);
        }

        await ExtRegistry.Remove(sessionId);
    }
});

// Extension control channel: relays JSON commands/events between the WireCopy.API child (extension
// mode) and the Chrome extension's background service worker. See ExtSession for the protocol.
app.Map("/ws/ext", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    var sessionId = context.Request.Query["session"].ToString();
    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    await ExtBridgeRelay.RunAsync(sessionId, socket, log, context.RequestAborted);
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

    // workspace-yqt5.2: no server-side screencast exists in extension mode, so a stray pane client must
    // get a clean immediate close — not a 30s no-pane wait that leaves the SPA's pane blank-and-open.
    if (extensionMode)
    {
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "extension-mode", context.RequestAborted);
        return;
    }

    await WebPaneRelay.RunAsync(sessionId, socket, log, context.RequestAborted);
});

var url = Environment.GetEnvironmentVariable("WIRECOPY_WEB_URL") ?? "http://127.0.0.1:5099";

// Auto-open the tab once the host is up — unless suppressed (WIRECOPY_NO_OPEN) or there is no GUI
// (a Linux host with no DISPLAY, e.g. CI / a container), where launching a browser would just error.
app.Lifetime.ApplicationStarted.Register(() =>
{
    if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WIRECOPY_NO_OPEN")))
    {
        return;
    }

    if (OperatingSystem.IsLinux() && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
    {
        log.LogInformation("WireCopy web host listening on {Url} (no DISPLAY — open it manually)", url);
        return;
    }

    try
    {
        var openUrl = url.Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(openUrl) { UseShellExecute = true });
    }
    catch (Exception ex)
    {
        log.LogDebug(ex, "Could not auto-open the browser; open {Url} manually", url);
    }
});

app.Run(url);
