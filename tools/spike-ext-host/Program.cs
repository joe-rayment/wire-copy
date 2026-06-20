// Spike backend for workspace-lcmd: a minimal HTTP endpoint that runs the REAL WireCopy LinkExtractor
// on HTML posted by the Chrome extension's content script (the user's own browser's rendered DOM).
// Proves the existing .NET extraction logic can be reused with the host browser as the renderer.
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using WireCopy.Infrastructure.Browser;

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = 64 * 1024 * 1024);
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 64 * 1024 * 1024);
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));
var app = builder.Build();
app.UseCors();

using var lf = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Warning).AddSimpleConsole());
var extractor = new LinkExtractor(lf.CreateLogger<LinkExtractor>());

app.MapGet("/", () => "spike-ext-host up");

app.MapPost("/extract", async (ExtractReq req) =>
{
    var links = await extractor.ExtractLinksAsync(req.Html ?? string.Empty, req.Url ?? string.Empty);
    var outp = links
        .Where(l => !l.IsGroupHeader)
        .Select(l => new
        {
            url = l.Url,
            text = l.DisplayText,
            type = l.Type.ToString(),
            score = l.ImportanceScore,
            external = l.IsExternal,
            section = l.SectionTitle,
        })
        .ToList();
    return Results.Json(new { count = outp.Count, links = outp });
});

app.Run("http://127.0.0.1:5181");

record ExtractReq(string? Url, string? Html);
