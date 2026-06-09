// Licensed under the MIT License. See LICENSE in the repository root.

using System.Diagnostics;
using System.Text;
using NSubstitute;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Domain.Entities.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Browser;
using WireCopy.Infrastructure.Browser.Themes;
using WireCopy.Infrastructure.Browser.UI.Renderers;
using Xunit;
using Xunit.Abstractions;

namespace WireCopy.Tests.Browser;

/// <summary>
/// Profiling harness (not a correctness test) for the reader-view render hot path.
/// Measures the one-time line-cache build cost and the per-frame emit cost that
/// is paid on every speed-read tick / scroll / keystroke, plus the bytes emitted
/// per frame (the over-SSH cost). Prints numbers via ITestOutputHelper; asserts
/// only loose non-zero sanity so it can't flake on timing.
///
/// Run with:
///   dotnet test --filter "FullyQualifiedName~RenderPerfBenchmark"
/// </summary>
[Trait("Category", "Benchmark")]
[Collection(WireCopy.Tests.ConsoleSerialCollection.Name)]
public class RenderPerfBenchmark
{
    private readonly ITestOutputHelper _out;

    public RenderPerfBenchmark(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Reader_FrameEmit_And_LineCacheBuild_Cost()
    {
        // --- Representative long-form article: ~60 paragraphs * ~55 words = ~3.3k words ---
        const int paragraphCount = 60;
        const int wordsPerParagraph = 55;
        const int maxContentWidth = 60; // "Comfortable" reader default
        var paragraphs = BuildParagraphs(paragraphCount, wordsPerParagraph);
        var content = ReadableContent.Create(
            "A Fairly Representative Long-Form Article Headline For Benchmarking",
            string.Join("\n\n", paragraphs),
            paragraphs,
            author: "Jane Q. Reporter",
            publishedDate: new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc));

        var palette = BuiltInThemes.Get(ThemeName.Phosphor);

        // --- Measure line-cache build (one-time per article load and per resize) ---
        const int buildIterations = 200;
        var buildSw = Stopwatch.StartNew();
        List<string> allLines = new();
        List<LineCacheManager.ParagraphSpan> spans = new();
        for (var i = 0; i < buildIterations; i++)
        {
            (allLines, spans) = BuildLineCache(content, maxContentWidth, palette);
        }

        buildSw.Stop();
        var buildMs = buildSw.Elapsed.TotalMilliseconds / buildIterations;

        // --- Set up the per-frame emit path exactly as the reader uses it ---
        var themeProvider = Substitute.For<WireCopy.Application.Interfaces.Browser.IThemeProvider>();
        themeProvider.CurrentTheme.Returns(ThemeName.Phosphor);
        var helpers = new RenderHelpers { TerminalHeight = 50 };
        var article = new ArticleRenderer(helpers, themeProvider);

        var options = new RenderOptions { TerminalWidth = 100, TerminalHeight = 50, MaxContentWidth = maxContentWidth };
        var viewportHeight = options.TerminalHeight - 3;
        var context = new NavigationContext
        {
            ViewMode = ViewMode.Readable,
            ScrollOffset = 0,
            ReaderCursorLine = 15,
        };

        var originalOut = Console.Out;
        double emitMs;
        int frameBytes;
        try
        {
            // One frame to a StringWriter to measure emitted bytes.
            using (var sw = new StringWriter())
            {
                Console.SetOut(sw);
                helpers.BeginFrame();
                article.RenderLineBasedContent(allLines, context, viewportHeight, options, spans);
                helpers.EndFrame();
                frameBytes = Encoding.UTF8.GetByteCount(sw.ToString());
            }

            // Time N frames to TextWriter.Null (the per-frame emit work, atomic-buffered).
            Console.SetOut(TextWriter.Null);
            const int warmup = 200;
            const int iterations = 4000;
            for (var i = 0; i < warmup; i++)
            {
                helpers.BeginFrame();
                article.RenderLineBasedContent(allLines, context, viewportHeight, options, spans);
                helpers.EndFrame();
            }

            var emitSw = Stopwatch.StartNew();
            for (var i = 0; i < iterations; i++)
            {
                helpers.BeginFrame();
                article.RenderLineBasedContent(allLines, context, viewportHeight, options, spans);
                helpers.EndFrame();
            }

            emitSw.Stop();
            emitMs = emitSw.Elapsed.TotalMilliseconds / iterations;
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        // --- Report ---
        var visibleLines = Math.Min(viewportHeight, allLines.Count);
        _out.WriteLine($"Article: {paragraphCount} paragraphs, {content.WordCount} words -> {allLines.Count} wrapped lines @ width {maxContentWidth}");
        _out.WriteLine($"Line-cache build (WrapAllContent + headline): {buildMs:F3} ms  [one-time per load / per resize]");
        _out.WriteLine($"Per-frame emit: {emitMs:F4} ms/frame, {frameBytes} bytes/frame ({visibleLines} visible lines)");
        _out.WriteLine("--- projected per-frame cost at various repaint cadences ---");
        foreach (var fps in new[] { 2, 10, 30 })
        {
            _out.WriteLine(
                $"  {fps,2} repaints/s: CPU {emitMs * fps:F3} ms/s ({emitMs * fps / 10.0:F2}% of one core), output {frameBytes * fps / 1024.0:F1} KB/s");
        }

        _out.WriteLine("(speed-read @ 750 WPM advances ~1-2 lines/s; held-key scroll ~30/s)");

        // Loose sanity only — never assert on wall-clock.
        Assert.True(allLines.Count > 50);
        Assert.True(frameBytes > 0);
        Assert.True(emitMs >= 0);
    }

    private static (List<string> Lines, List<LineCacheManager.ParagraphSpan> Spans) BuildLineCache(
        ReadableContent content, int maxWidth, ThemePalette palette)
    {
        var headline = LineCacheManager.BuildHeadlineLines(content, maxWidth, palette, "https://news.example.com/article");
        var (contentLines, rawSpans) = LineCacheManager.WrapAllContentWithSpans(content, maxWidth);
        var headlineCount = headline.Count;
        var spans = new List<LineCacheManager.ParagraphSpan>(rawSpans.Count);
        foreach (var s in rawSpans)
        {
            spans.Add(new LineCacheManager.ParagraphSpan(s.StartLine + headlineCount, s.EndLine + headlineCount));
        }

        headline.AddRange(contentLines);
        return (headline, spans);
    }

    private static List<string> BuildParagraphs(int count, int wordsPerParagraph)
    {
        // Deterministic pseudo-words of varied length (no RNG so runs are stable).
        var vocab = new[]
        {
            "the", "market", "reportedly", "announced", "a", "significant", "restructuring",
            "following", "quarterly", "results", "that", "analysts", "described", "as",
            "unexpected", "given", "prevailing", "conditions", "across", "the", "sector",
            "investors", "weighed", "implications", "for", "longer-term", "growth",
        };
        var paragraphs = new List<string>(count);
        var k = 0;
        for (var pIdx = 0; pIdx < count; pIdx++)
        {
            var sb = new StringBuilder(wordsPerParagraph * 8);
            for (var w = 0; w < wordsPerParagraph; w++)
            {
                if (w > 0)
                {
                    sb.Append(' ');
                }

                sb.Append(vocab[k % vocab.Length]);
                k++;
            }

            sb.Append('.');
            paragraphs.Add(sb.ToString());
        }

        return paragraphs;
    }
}
