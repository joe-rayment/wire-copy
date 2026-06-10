// Licensed under the MIT License. See LICENSE in the repository root.

using System.Net;
using Microsoft.Extensions.Logging;

namespace WireCopy.Infrastructure.Demo;

/// <summary>
/// workspace-kt19.2 — a tiny localhost static file server for the bundled
/// public-domain demo site (demo/site → The Daily Gazette). Listens on a fixed
/// loopback origin so demo bookmarks are stable strings; serves only GET/HEAD
/// from a single content root with strict path containment.
/// </summary>
public sealed class DemoSiteServer : IDisposable
{
    /// <summary>Fixed loopback port the demo bookmarks point at.</summary>
    public const int Port = 8642;

    /// <summary>Origin prefix all demo bookmark URLs share.</summary>
    public const string Origin = "http://127.0.0.1:8642/";

    private readonly string _root;
    private readonly ILogger _logger;
    private readonly HttpListener _listener = new();

    public DemoSiteServer(string contentRoot, ILogger logger)
    {
        _root = Path.GetFullPath(contentRoot);
        _logger = logger;
        _listener.Prefixes.Add(Origin);
    }

    /// <summary>
    /// Locates the demo site content. Probes the build output copy
    /// (<c>demo-site/</c> beside the binary) first, then walks up from the
    /// binary and the working directory looking for a repo-checkout
    /// <c>demo/site/</c>. Returns null when no pack is present.
    /// </summary>
    public static string? ResolveContentRoot()
    {
        var probes = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "demo-site"),
        };
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
            {
                probes.Add(Path.Combine(dir.FullName, "demo", "site"));
            }
        }

        return probes.FirstOrDefault(p => File.Exists(Path.Combine(p, "index.html")));
    }

    public void Start()
    {
        _listener.Start();
        _ = Task.Run(AcceptLoopAsync);
        _logger.LogInformation("Demo site server: serving {Root} at {Origin}", _root, Origin);
    }

    public void Dispose()
    {
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch
        {
            // Best-effort shutdown.
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (!_listener.IsListening)
            {
                return; // disposed
            }

            _ = Task.Run(() => ServeAsync(ctx));
        }
    }

    private async Task ServeAsync(HttpListenerContext ctx)
    {
        try
        {
            var rel = Uri.UnescapeDataString(ctx.Request.Url?.AbsolutePath ?? "/").TrimStart('/');
            if (rel.Length == 0)
            {
                rel = "index.html";
            }

            var full = Path.GetFullPath(Path.Combine(_root, rel));
            if (!full.StartsWith(_root, StringComparison.Ordinal) || !File.Exists(full)
                || (ctx.Request.HttpMethod != "GET" && ctx.Request.HttpMethod != "HEAD"))
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
                return;
            }

            ctx.Response.ContentType = Path.GetExtension(full).ToLowerInvariant() switch
            {
                ".html" => "text/html; charset=utf-8",
                ".css" => "text/css; charset=utf-8",
                _ => "text/plain; charset=utf-8",
            };
            var bytes = await File.ReadAllBytesAsync(full).ConfigureAwait(false);
            ctx.Response.ContentLength64 = bytes.Length;
            if (ctx.Request.HttpMethod == "GET")
            {
                await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
            }

            ctx.Response.Close();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Demo site server: request failed");
            try
            {
                ctx.Response.StatusCode = 500;
                ctx.Response.Close();
            }
            catch
            {
                // Response already gone.
            }
        }
    }
}
