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
        bool hasApiKey = true)
    {
        var hierConfig = Options.Create(new OpenAiHierarchyConfiguration { Model = "gpt-test" });
        var ttsConfig = Options.Create(new OpenAiTtsConfiguration
        {
            ApiKey = hasApiKey ? "sk-test-fake" : string.Empty,
        });
        var settings = Substitute.For<IUserSettingsStore>();
        settings.Get(Arg.Any<string>()).Returns((string?)null);
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
            (key, model, messages, options, ct) => Task.FromResult(FakeResponse);

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
            (key, model, messages, options, ct) => Task.FromResult(FakeResponse);

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
                    return Task.FromResult("not json at all");
                }

                return Task.FromResult("""
                    {
                      "name": "article",
                      "pageType": "Article",
                      "priority": 10,
                      "matcher": { "urlPattern": null, "ldJsonType": null, "bodyClassContains": null },
                      "selectors": { "headline": [], "byline": [], "publishDate": [], "body": [], "excludeRegions": [] },
                      "quality": { "minWords": 100, "minParagraphs": 3 }
                    }
                    """);
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
            (key, model, messages, options, ct) => Task.FromResult("{}");

        var sut = MakeExtractor(completer);

        var result = await sut.AnalyzeAsync("not-a-url", "<html/>", CancellationToken.None);

        result.Should().BeNull();
    }
}
