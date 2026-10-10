// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.Logging;
using WireCopy.Infrastructure.Browser;
using Xunit;

namespace WireCopy.Tests.Browser;

[Trait("Category", "Unit")]
public class LinkExtractorNonWebSchemeTests
{
    // Guards:   mailto:/tel: footer links are dropped from the link list without writing a Warning
    //           to the log (they are routine on real pages), while real story links are still kept.
    // Fails if: ResolveUrl logs "Failed to resolve URL" at Warning for non-http(s) hrefs again, or
    //           the fix starts dropping relative story links such as "/news/budget-vote".
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~LinkExtractorNonWebSchemeTests"
    [Fact]
    public async Task ExtractLinks_MailtoAndTelLinks_AreDroppedWithoutWarning_StoryLinksKept()
    {
        var logger = new RecordingLogger();
        var extractor = new LinkExtractor(logger);
        const string html = """
            <html><body>
                <main>
                    <a href="https://example.com/news/city-council-passes-budget">City council passes the annual budget after a long debate</a>
                    <a href="/news/budget-vote">Residents react to the budget vote at city hall</a>
                </main>
                <footer>
                    <a href="mailto:a@b.c">Email us</a>
                    <a href="tel:+15551234567">Call the newsroom</a>
                </footer>
            </body></html>
            """;

        var links = await extractor.ExtractLinksAsync(html, "https://example.com/");

        links.Select(l => l.Url).Should().BeEquivalentTo(
            "https://example.com/news/city-council-passes-budget",
            "https://example.com/news/budget-vote");
        logger.Levels.Should().NotContain(LogLevel.Warning, "mailto:/tel: links are expected, not problems");
        logger.Levels.Should().NotContain(LogLevel.Error);
    }

    private sealed class RecordingLogger : ILogger<LinkExtractor>
    {
        public List<LogLevel> Levels { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Levels.Add(logLevel);
    }
}
