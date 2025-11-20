// <copyright file="FilteringTextWriter.cs" company="NYT Audio Scraper">
// Educational and personal use only.
// </copyright>

using System.Text;

namespace NYTAudioScraper.API;

/// <summary>
/// A TextWriter that filters out browser noise from stderr by default.
/// Suppresses geckodriver, Firefox, and Chrome console output.
/// </summary>
public class FilteringTextWriter : TextWriter
{
    private readonly TextWriter _originalWriter;
    private readonly bool _verbose;

    // Patterns to filter out (browser noise)
    private static readonly string[] FilterPatterns =
    [
        "console.error:",
        "NS_ERROR_CONTENT_BLOCKED",
        "geckodriver",
        "You are running in headless mode",
        "Read port:",
        "RenderCompositorSWGL",
        "AVCaptureDeviceTypeExternal",
        "FaviconLoader.sys.mjs",
        "getpocket.com",
        "OHTTP was configured",
        "ContextId.sys.mjs",
        "[GFX",
        "firefox[",
        "TypeError",
        "NetworkError",
        "Failed to fetch"
    ];

    public FilteringTextWriter(TextWriter originalWriter, bool verbose)
    {
        _originalWriter = originalWriter;
        _verbose = verbose;
    }

    public override Encoding Encoding => _originalWriter.Encoding;

    public override void Write(char value)
    {
        _originalWriter.Write(value);
    }

    public override void Write(string? value)
    {
        if (_verbose || value == null || !ShouldFilter(value))
        {
            _originalWriter.Write(value);
        }
    }

    public override void WriteLine(string? value)
    {
        if (_verbose || value == null || !ShouldFilter(value))
        {
            _originalWriter.WriteLine(value);
        }
    }

    private static bool ShouldFilter(string value)
    {
        // Filter out lines containing any of the browser noise patterns
        return FilterPatterns.Any(pattern =>
            value.Contains(pattern, StringComparison.OrdinalIgnoreCase));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _originalWriter.Dispose();
        }
        base.Dispose(disposing);
    }
}
