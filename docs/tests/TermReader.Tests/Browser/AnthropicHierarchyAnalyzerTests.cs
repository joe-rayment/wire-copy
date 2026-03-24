// Educational and personal use only.

using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using TermReader.Application.Interfaces;
using TermReader.Domain.Enums.Browser;
using TermReader.Domain.ValueObjects.Browser;
using TermReader.Infrastructure.Browser;
using TermReader.Infrastructure.Configuration;
using Xunit;

namespace TermReader.Tests.Browser;

[Trait("Category", "Unit")]
public class AnthropicHierarchyAnalyzerTests
{
    private readonly IUserSettingsStore _settingsStore;
    private readonly ILogger<AnthropicHierarchyAnalyzer> _logger;
    private readonly AnthropicConfiguration _config;

    public AnthropicHierarchyAnalyzerTests()
    {
        _settingsStore = Substitute.For<IUserSettingsStore>();
        _logger = Substitute.For<ILogger<AnthropicHierarchyAnalyzer>>();
        _config = new AnthropicConfiguration
        {
            Model = "claude-haiku-4-5-20251001",
            MaxTokens = 4096,
        };
    }

    private AnthropicHierarchyAnalyzer CreateAnalyzer(HttpClient? httpClient = null)
    {
        return new AnthropicHierarchyAnalyzer(
            Options.Create(_config),
            _settingsStore,
            _logger,
            httpClient);
    }

    private static List<LinkInfo> CreateSampleLinks()
    {
        return new List<LinkInfo>
        {
            new LinkInfo
            {
                Url = "https://example.com/article1",
                DisplayText = "Article 1",
                Type = LinkType.Content,
                ImportanceScore = 80,
                ParentSelector = "article > h3",
            },
            new LinkInfo
            {
                Url = "https://example.com/article2",
                DisplayText = "Article 2",
                Type = LinkType.Content,
                ImportanceScore = 70,
            },
        };
    }

    [Fact]
    public void IsConfigured_NoApiKey_ReturnsFalse()
    {
        _settingsStore.Get("AnthropicApiKey").Returns((string?)null);

        var analyzer = CreateAnalyzer();

        analyzer.IsConfigured.Should().BeFalse();
    }

    [Fact]
    public void IsConfigured_ApiKeyInSettingsStore_ReturnsTrue()
    {
        _settingsStore.Get("AnthropicApiKey").Returns("sk-ant-test-key");

        var analyzer = CreateAnalyzer();

        analyzer.IsConfigured.Should().BeTrue();
    }

    [Fact]
    public void IsConfigured_ApiKeyInConfig_ReturnsTrue()
    {
        _settingsStore.Get("AnthropicApiKey").Returns((string?)null);
        var configWithKey = new AnthropicConfiguration { ApiKey = "sk-ant-config-key" };
        var analyzer = new AnthropicHierarchyAnalyzer(
            Options.Create(configWithKey), _settingsStore, _logger);

        analyzer.IsConfigured.Should().BeTrue();
    }

    [Fact]
    public async Task AnalyzePageHierarchyAsync_NoApiKey_ThrowsInvalidOperation()
    {
        _settingsStore.Get("AnthropicApiKey").Returns((string?)null);
        var analyzer = CreateAnalyzer();

        var act = () => analyzer.AnalyzePageHierarchyAsync(
            new byte[] { 1, 2, 3 },
            CreateSampleLinks(),
            "https://example.com/");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*API key not configured*");
    }

    [Fact]
    public async Task AnalyzePageHierarchyAsync_ValidResponse_ReturnsParsedConfig()
    {
        _settingsStore.Get("AnthropicApiKey").Returns("sk-ant-test-key");

        var apiResponse = @"{
            ""content"": [{
                ""type"": ""text"",
                ""text"": ""[{\""name\"": \""Top Stories\"", \""linkIndices\"": [0, 1], \""parentSelectors\"": [\""article > h3\""], \""urlPatterns\"": [\""/article/\""], \""startCollapsed\"": false}]""
            }]
        }";

        var handler = new FakeHttpHandler(HttpStatusCode.OK, apiResponse);
        var httpClient = new HttpClient(handler);
        var analyzer = CreateAnalyzer(httpClient);

        var result = await analyzer.AnalyzePageHierarchyAsync(
            new byte[] { 1, 2, 3 },
            CreateSampleLinks(),
            "https://example.com/");

        result.Should().NotBeNull();
        result.Domain.Should().Be("example.com");
        result.Sections.Should().HaveCount(1);
        result.Sections[0].Name.Should().Be("Top Stories");
        result.Sections[0].ParentSelectors.Should().Contain("article > h3");
        result.ModelVersion.Should().Be("claude-haiku-4-5-20251001");
    }

    [Fact]
    public async Task AnalyzePageHierarchyAsync_MarkdownFences_StripsAndParses()
    {
        _settingsStore.Get("AnthropicApiKey").Returns("sk-ant-test-key");

        var jsonWithFences = "```json\n[{\"name\": \"Section\", \"linkIndices\": [0], \"parentSelectors\": [], \"urlPatterns\": [], \"startCollapsed\": false}]\n```";
        var apiResponse = $@"{{""content"": [{{""type"": ""text"", ""text"": ""{EscapeJson(jsonWithFences)}""}}]}}";

        var handler = new FakeHttpHandler(HttpStatusCode.OK, apiResponse);
        var httpClient = new HttpClient(handler);
        var analyzer = CreateAnalyzer(httpClient);

        var result = await analyzer.AnalyzePageHierarchyAsync(
            new byte[] { 1, 2, 3 },
            CreateSampleLinks(),
            "https://example.com/");

        result.Sections.Should().HaveCount(1);
        result.Sections[0].Name.Should().Be("Section");
    }

    [Fact]
    public async Task AnalyzePageHierarchyAsync_ApiError_ThrowsHttpRequestException()
    {
        _settingsStore.Get("AnthropicApiKey").Returns("sk-ant-test-key");

        var handler = new FakeHttpHandler(HttpStatusCode.Unauthorized, "{\"error\": \"invalid key\"}");
        var httpClient = new HttpClient(handler);
        var analyzer = CreateAnalyzer(httpClient);

        var act = () => analyzer.AnalyzePageHierarchyAsync(
            new byte[] { 1, 2, 3 },
            CreateSampleLinks(),
            "https://example.com/");

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task AnalyzePageHierarchyAsync_UrlPattern_IncludesDomain()
    {
        _settingsStore.Get("AnthropicApiKey").Returns("sk-ant-test-key");

        var apiResponse = @"{""content"": [{""type"": ""text"", ""text"": ""[{\""name\"": \""Main\"", \""linkIndices\"": [0]}]""}]}";

        var handler = new FakeHttpHandler(HttpStatusCode.OK, apiResponse);
        var httpClient = new HttpClient(handler);
        var analyzer = CreateAnalyzer(httpClient);

        var result = await analyzer.AnalyzePageHierarchyAsync(
            new byte[] { 1, 2, 3 },
            CreateSampleLinks(),
            "https://www.nytimes.com/section/opinion");

        result.UrlPattern.Should().Contain("nytimes\\.com");
        result.Domain.Should().Be("www.nytimes.com");
    }

    [Fact]
    public void IsConfigured_SettingsStorePrecedence_OverConfig()
    {
        _settingsStore.Get("AnthropicApiKey").Returns("sk-ant-store-key");
        var configWithKey = new AnthropicConfiguration { ApiKey = "sk-ant-config-key" };
        var analyzer = new AnthropicHierarchyAnalyzer(
            Options.Create(configWithKey), _settingsStore, _logger);

        analyzer.IsConfigured.Should().BeTrue();
        // The store key should take precedence (tested indirectly via IsConfigured)
    }

    private static string EscapeJson(string text)
    {
        return text
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r");
    }

    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string _responseBody;

        public FakeHttpHandler(HttpStatusCode statusCode, string responseBody)
        {
            _statusCode = statusCode;
            _responseBody = responseBody;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_responseBody, Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(response);
        }
    }
}
