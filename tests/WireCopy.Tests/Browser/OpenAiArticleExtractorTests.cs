// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using OpenAI.Chat;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Infrastructure.Browser;
using WireCopy.Infrastructure.Configuration;
using Xunit;

namespace WireCopy.Tests.Browser;

[Trait("Category", "Unit")]
public class OpenAiArticleExtractorTests
{
    private static OpenAiArticleExtractor MakeExtractor(
        OpenAiArticleExtractor.ChatCompleter? completer,
        bool hasApiKey = true,
        int monthlyTokenBudget = 200_000,
        IUserSettingsStore? settingsStoreOverride = null)
    {
        var hierConfig = Options.Create(new OpenAiHierarchyConfiguration
        {
            Model = "gpt-test",
            ArticleModel = "gpt-test", // workspace-r8on: extractor now reads ArticleModel
            MonthlyTokenBudget = monthlyTokenBudget,
        });
        var ttsConfig = Options.Create(new OpenAiTtsConfiguration
        {
            ApiKey = hasApiKey ? "sk-test-fake" : string.Empty,
        });
        IUserSettingsStore settings;
        if (settingsStoreOverride != null)
        {
            settings = settingsStoreOverride;
        }
        else
        {
            settings = Substitute.For<IUserSettingsStore>();
            settings.Get(Arg.Any<string>()).Returns((string?)null);
        }

        var logger = Substitute.For<ILogger<OpenAiArticleExtractor>>();

        return new OpenAiArticleExtractor(hierConfig, ttsConfig, settings, logger, completer);
    }

    [Fact]
    public void IsConfigured_WithApiKey_True()
    {
        var sut = MakeExtractor(completer: null, hasApiKey: true);

        sut.IsConfigured.Should().BeTrue();
    }

    [Fact]
    public void IsConfigured_WithoutApiKey_False()
    {
        var sut = MakeExtractor(completer: null, hasApiKey: false);

        sut.IsConfigured.Should().BeFalse();
    }

    [Fact]
    public async Task AnalyzeAsync_WithoutApiKey_ReturnsNull()
    {
        var sut = MakeExtractor(completer: null, hasApiKey: false);

        var result = await sut.AnalyzeAsync(
            "https://example.com/2026/05/09/story",
            "<html><body><p>x</p></body></html>",
            CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task AnalyzeAsync_ParsesValidJsonResponse()
    {
        const string FakeResponse = """
            {
              "name": "article",
              "pageType": "Article",
              "priority": 10,
              "matcher": {
                "urlPattern": "/2026/",
                "ldJsonType": "NewsArticle",
                "bodyClassContains": "story"
              },
              "selectors": {
                "headline": ["//h1"],
                "byline": ["//span[@class='byline']"],
                "publishDate": ["//time[@datetime]"],
                "body": ["//article"],
                "excludeRegions": ["//div[@class='ad']"]
              },
              "quality": {
                "minWords": 200,
                "minParagraphs": 4
              }
            }
            """;

        OpenAiArticleExtractor.ChatCompleter completer =
            (key, model, messages, options, ct) => Task.FromResult(new ChatCompletionResult(FakeResponse, 0));

        var sut = MakeExtractor(completer);

        var result = await sut.AnalyzeAsync(
            "https://example.com/2026/05/09/story",
            "<html><body></body></html>",
            CancellationToken.None);

        result.Should().NotBeNull();
        result!.Domain.Should().Be("example.com");
        result.SchemaVersion.Should().Be(ArticleLayoutStore.CurrentSchemaVersion);
        result.PageTypes.Should().HaveCount(1);

        var entry = result.PageTypes[0];
        entry.Name.Should().Be("article");
        entry.PageType.Should().Be(PageType.Article);
        entry.Priority.Should().Be(10);
        entry.Matcher.UrlPattern.Should().Be("/2026/");
        entry.Matcher.LdJsonType.Should().Be("NewsArticle");
        entry.Matcher.BodyClassContains.Should().Be("story");
        entry.Selectors.Headline.Should().ContainSingle().Which.Should().Be("//h1");
        entry.Selectors.ExcludeRegions.Should().ContainSingle().Which.Should().Be("//div[@class='ad']");
        entry.Quality.MinWords.Should().Be(200);
        entry.Quality.MinParagraphs.Should().Be(4);
        entry.Provenance.Model.Should().Be("gpt-test");
        entry.Provenance.SampleUrl.Should().Be("https://example.com/2026/05/09/story");
    }

    [Fact]
    public async Task AnalyzeAsync_StripsMarkdownFences()
    {
        const string FakeResponse = """
            ```json
            {
              "name": "article",
              "pageType": "Article",
              "priority": 10,
              "matcher": { "urlPattern": null, "ldJsonType": null, "bodyClassContains": null },
              "selectors": { "headline": [], "byline": [], "publishDate": [], "body": [], "excludeRegions": [] },
              "quality": { "minWords": 100, "minParagraphs": 3 }
            }
            ```
            """;

        OpenAiArticleExtractor.ChatCompleter completer =
            (key, model, messages, options, ct) => Task.FromResult(new ChatCompletionResult(FakeResponse, 0));

        var sut = MakeExtractor(completer);

        var result = await sut.AnalyzeAsync(
            "https://example.com/story",
            "<html/>",
            CancellationToken.None);

        result.Should().NotBeNull();
        result!.PageTypes.Should().HaveCount(1);
    }

    [Fact]
    public async Task AnalyzeAsync_RetriesOnceAfterMalformedJson()
    {
        var calls = 0;
        OpenAiArticleExtractor.ChatCompleter completer =
            (key, model, messages, options, ct) =>
            {
                calls++;
                if (calls == 1)
                {
                    return Task.FromResult(new ChatCompletionResult("not json at all", 0));
                }

                return Task.FromResult(new ChatCompletionResult(
                    """
                    {
                      "name": "article",
                      "pageType": "Article",
                      "priority": 10,
                      "matcher": { "urlPattern": null, "ldJsonType": null, "bodyClassContains": null },
                      "selectors": { "headline": [], "byline": [], "publishDate": [], "body": [], "excludeRegions": [] },
                      "quality": { "minWords": 100, "minParagraphs": 3 }
                    }
                    """,
                    0));
            };

        var sut = MakeExtractor(completer);

        var result = await sut.AnalyzeAsync("https://example.com/story", "<html/>", CancellationToken.None);

        calls.Should().Be(2, "the extractor must retry once on JsonException");
        result.Should().NotBeNull();
    }

    [Fact]
    public async Task AnalyzeAsync_InvalidUrl_ReturnsNull()
    {
        OpenAiArticleExtractor.ChatCompleter completer =
            (key, model, messages, options, ct) => Task.FromResult(new ChatCompletionResult("{}", 0));

        var sut = MakeExtractor(completer);

        var result = await sut.AnalyzeAsync("not-a-url", "<html/>", CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task AnalyzeAsync_StripsScriptStyleAndIframeFromPrompt()
    {
        var capturedPrompt = string.Empty;
        OpenAiArticleExtractor.ChatCompleter completer =
            (key, model, messages, options, ct) =>
            {
                capturedPrompt = string.Join("\n", messages.OfType<UserChatMessage>()
                    .Select(m => string.Join(string.Empty, m.Content.Select(p => p.Text))));
                return Task.FromResult(new ChatCompletionResult(MinimalFakeResponse, 0));
            };

        var sut = MakeExtractor(completer);

        const string Html = """
            <html>
              <head>
                <script>window.dataLayer = [];</script>
                <style>.ad { display: none; }</style>
                <noscript>Enable JavaScript</noscript>
                <script type="application/ld+json">{"@type":"NewsArticle"}</script>
              </head>
              <body>
                <iframe src="https://ads.example.com/banner"></iframe>
                <svg><path/></svg>
                <article><p>Real content.</p></article>
              </body>
            </html>
            """;

        await sut.AnalyzeAsync("https://example.com/story", Html, CancellationToken.None);

        capturedPrompt.Should().NotContain("dataLayer", "inline scripts must be stripped");
        capturedPrompt.Should().NotContain(".ad { display: none; }", "style blocks must be stripped");
        capturedPrompt.Should().NotContain("Enable JavaScript", "noscript blocks must be stripped");
        capturedPrompt.Should().NotContain("ads.example.com/banner", "iframe must be stripped");
        capturedPrompt.Should().NotContain("<svg", "svg must be stripped");
        capturedPrompt.Should().Contain("@type\":\"NewsArticle", "ld+json scripts are matcher signals and must be preserved");
        capturedPrompt.Should().Contain("Real content.", "article body must survive sanitisation");
    }

    [Fact]
    public async Task AnalyzeAsync_RedactsBase64DataUrisInPrompt()
    {
        var capturedPrompt = string.Empty;
        OpenAiArticleExtractor.ChatCompleter completer =
            (key, model, messages, options, ct) =>
            {
                capturedPrompt = string.Join("\n", messages.OfType<UserChatMessage>()
                    .Select(m => string.Join(string.Empty, m.Content.Select(p => p.Text))));
                return Task.FromResult(new ChatCompletionResult(MinimalFakeResponse, 0));
            };

        var sut = MakeExtractor(completer);

        const string Html = """
            <html><body>
              <img src="data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8/5+hHgAHggJ/PchI7wAAAABJRU5ErkJggg==">
              <a href="data:text/html;base64,PGgxPnBvaXNvbjwvaDE+">link</a>
              <p>Real text.</p>
            </body></html>
            """;

        await sut.AnalyzeAsync("https://example.com/story", Html, CancellationToken.None);

        capturedPrompt.Should().NotContain("iVBORw0KGgo", "base64 image data must be redacted");
        capturedPrompt.Should().NotContain("PGgxPnBvaXNvbjwvaDE", "base64 link data must be redacted");
        capturedPrompt.Should().Contain("data:redacted", "the redaction marker must replace base64 payloads");
        capturedPrompt.Should().Contain("Real text.", "regular content must survive");
    }

    [Fact]
    public async Task AnalyzeAsync_RemovesTrackerPixelsFromPrompt()
    {
        var capturedPrompt = string.Empty;
        OpenAiArticleExtractor.ChatCompleter completer =
            (key, model, messages, options, ct) =>
            {
                capturedPrompt = string.Join("\n", messages.OfType<UserChatMessage>()
                    .Select(m => string.Join(string.Empty, m.Content.Select(p => p.Text))));
                return Task.FromResult(new ChatCompletionResult(MinimalFakeResponse, 0));
            };

        var sut = MakeExtractor(completer);

        const string Html = """
            <html><body>
              <img src="https://www.google-analytics.com/collect?v=1" />
              <img src="https://stats.g.doubleclick.net/r/collect?v=1" />
              <img width="1" height="1" src="/spacer.gif" />
              <img src="/hero.jpg" alt="hero photo" />
              <p>Story body.</p>
            </body></html>
            """;

        await sut.AnalyzeAsync("https://example.com/story", Html, CancellationToken.None);

        capturedPrompt.Should().NotContain("google-analytics.com", "tracker hosts must be removed");
        capturedPrompt.Should().NotContain("doubleclick.net", "tracker hosts must be removed");
        capturedPrompt.Should().NotContain("/spacer.gif", "1x1 pixels must be removed");
        capturedPrompt.Should().Contain("/hero.jpg", "regular images must survive");
        capturedPrompt.Should().Contain("Story body.", "regular content must survive");
    }

    [Fact]
    public async Task AnalyzeAsync_TruncatesOversizedHtml()
    {
        var capturedPromptLength = 0;
        OpenAiArticleExtractor.ChatCompleter completer =
            (key, model, messages, options, ct) =>
            {
                capturedPromptLength = string.Join(string.Empty, messages.OfType<UserChatMessage>()
                    .SelectMany(m => m.Content.Select(p => p.Text))).Length;
                return Task.FromResult(new ChatCompletionResult(MinimalFakeResponse, 0));
            };

        var sut = MakeExtractor(completer);

        // Build an HTML payload well above the 80KB cap.
        var bigBody = new string('x', 200_000);
        var html = $"<html><body><p>{bigBody}</p></body></html>";

        await sut.AnalyzeAsync("https://example.com/story", html, CancellationToken.None);

        capturedPromptLength.Should().BeLessThan(120_000, "the prompt must be truncated to keep token cost bounded");
    }

    [Fact]
    public async Task AnalyzeAsync_RecordsTokensInSettingsStore()
    {
        var settings = new InMemorySettingsStore();
        OpenAiArticleExtractor.ChatCompleter completer =
            (key, model, messages, options, ct) =>
                Task.FromResult(new ChatCompletionResult(MinimalFakeResponse, TotalTokens: 1234));

        var sut = MakeExtractor(completer, settingsStoreOverride: settings);

        await sut.AnalyzeAsync("https://example.com/story", "<html/>", CancellationToken.None);

        var period = DateTime.UtcNow.ToString("yyyyMM", System.Globalization.CultureInfo.InvariantCulture);
        var key = $"AiArticleTokens:{period}";
        settings.Get(key).Should().Be("1234");
    }

    [Fact]
    public async Task AnalyzeAsync_AccumulatesTokensAcrossCalls()
    {
        var settings = new InMemorySettingsStore();
        OpenAiArticleExtractor.ChatCompleter completer =
            (key, model, messages, options, ct) =>
                Task.FromResult(new ChatCompletionResult(MinimalFakeResponse, TotalTokens: 500));

        var sut = MakeExtractor(completer, settingsStoreOverride: settings);

        await sut.AnalyzeAsync("https://example.com/story1", "<html/>", CancellationToken.None);
        await sut.AnalyzeAsync("https://example.com/story2", "<html/>", CancellationToken.None);
        await sut.AnalyzeAsync("https://example.com/story3", "<html/>", CancellationToken.None);

        var period = DateTime.UtcNow.ToString("yyyyMM", System.Globalization.CultureInfo.InvariantCulture);
        settings.Get($"AiArticleTokens:{period}").Should().Be("1500");
    }

    [Fact]
    public async Task AnalyzeAsync_BudgetExceeded_SkipsCall()
    {
        var settings = new InMemorySettingsStore();
        var period = DateTime.UtcNow.ToString("yyyyMM", System.Globalization.CultureInfo.InvariantCulture);
        settings.Set($"AiArticleTokens:{period}", "9999");

        var calls = 0;
        OpenAiArticleExtractor.ChatCompleter completer =
            (key, model, messages, options, ct) =>
            {
                calls++;
                return Task.FromResult(new ChatCompletionResult(MinimalFakeResponse, 100));
            };

        var sut = MakeExtractor(
            completer,
            monthlyTokenBudget: 5000,
            settingsStoreOverride: settings);

        var result = await sut.AnalyzeAsync(
            "https://example.com/story",
            "<html/>",
            CancellationToken.None);

        result.Should().BeNull("the extractor must skip when the budget is exhausted");
        calls.Should().Be(0, "the chat completer must NOT be invoked over budget");
    }

    [Fact]
    public async Task AnalyzeAsync_BudgetZero_DisablesCap()
    {
        var settings = new InMemorySettingsStore();
        var period = DateTime.UtcNow.ToString("yyyyMM", System.Globalization.CultureInfo.InvariantCulture);
        settings.Set($"AiArticleTokens:{period}", "10000000");

        OpenAiArticleExtractor.ChatCompleter completer =
            (key, model, messages, options, ct) =>
                Task.FromResult(new ChatCompletionResult(MinimalFakeResponse, 100));

        var sut = MakeExtractor(
            completer,
            monthlyTokenBudget: 0,
            settingsStoreOverride: settings);

        var result = await sut.AnalyzeAsync(
            "https://example.com/story",
            "<html/>",
            CancellationToken.None);

        result.Should().NotBeNull("budget=0 disables the cap");
    }

    private sealed class InMemorySettingsStore : IUserSettingsStore
    {
        private readonly Dictionary<string, string> _data = new();

        public string? Get(string key) => _data.TryGetValue(key, out var v) ? v : null;

        public void Set(string key, string value, bool encrypt = false) => _data[key] = value;

        public void Remove(string key) => _data.Remove(key);
    }

    private const string MinimalFakeResponse = """
        {
          "name": "article",
          "pageType": "Article",
          "priority": 10,
          "matcher": { "urlPattern": null, "ldJsonType": null, "bodyClassContains": null },
          "selectors": { "headline": [], "byline": [], "publishDate": [], "body": [], "excludeRegions": [] },
          "quality": { "minWords": 100, "minParagraphs": 3 }
        }
        """;
}
