// Licensed under the MIT License. See LICENSE in the repository root.

using System.Net;
using System.Net.Http;
using FluentAssertions;
using WireCopy.Domain.ValueObjects.Podcast;
using WireCopy.Infrastructure.Podcast;
using Xunit;

namespace WireCopy.Tests.Podcast;

/// <summary>
/// Unit tests for the post-publish reachability probe (workspace-nb6b).
/// </summary>
[Trait("Category", "Unit")]
public class HttpFeedReachabilityProbeTests
{
    [Fact]
    public async Task ValidFeed_Returns_Ok()
    {
        var handler = new StubHandler(
            HttpStatusCode.OK,
            "application/rss+xml",
            "<?xml version=\"1.0\" encoding=\"utf-8\"?><rss version=\"2.0\"><channel><title>t</title></channel></rss>");
        var probe = new HttpFeedReachabilityProbe(new HttpClient(handler));

        var result = await probe.CheckAsync("https://example.com/feed.xml", default);

        result.FailureClass.Should().Be(FeedPublishFailureClass.None);
    }

    [Fact]
    public async Task Returns_FeedNotReachable_On403()
    {
        var handler = new StubHandler(HttpStatusCode.Forbidden, "text/html", "<html>forbidden</html>");
        var probe = new HttpFeedReachabilityProbe(new HttpClient(handler));

        var result = await probe.CheckAsync("https://example.com/feed.xml", default);

        result.FailureClass.Should().Be(FeedPublishFailureClass.FeedNotReachable);
        result.HttpStatusCode.Should().Be(403);
        result.Diagnostic.Should().Contain("403");
    }

    [Fact]
    public async Task Returns_FeedNotReachable_On500()
    {
        var handler = new StubHandler(HttpStatusCode.InternalServerError, "text/plain", "boom");
        var probe = new HttpFeedReachabilityProbe(new HttpClient(handler));

        var result = await probe.CheckAsync("https://example.com/feed.xml", default);

        result.FailureClass.Should().Be(FeedPublishFailureClass.FeedNotReachable);
        result.HttpStatusCode.Should().Be(500);
    }

    [Fact]
    public async Task Returns_FeedNotParseable_OnWrongContentType()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "text/html", "<html>browse redirect</html>");
        var probe = new HttpFeedReachabilityProbe(new HttpClient(handler));

        var result = await probe.CheckAsync("https://example.com/feed.xml", default);

        result.FailureClass.Should().Be(FeedPublishFailureClass.FeedNotParseable);
        result.ContentType.Should().Be("text/html");
    }

    [Fact]
    public async Task Returns_FeedNotParseable_OnInvalidXml()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "application/xml", "this is not <xml at all");
        var probe = new HttpFeedReachabilityProbe(new HttpClient(handler));

        var result = await probe.CheckAsync("https://example.com/feed.xml", default);

        result.FailureClass.Should().Be(FeedPublishFailureClass.FeedNotParseable);
        result.Diagnostic.Should().Contain("not parse");
    }

    [Fact]
    public async Task Catches_UtfLieScenarioFromWorkspaceJc2v()
    {
        // The bug we shipped a fix for: bytes are UTF-8 but the prolog claims utf-16.
        // The reachability probe should flag this because XDocument.Parse refuses
        // to read UTF-8 bytes when the declaration says UTF-16.
        var body = "<?xml version=\"1.0\" encoding=\"utf-16\"?>\n<rss><channel><title>t</title></channel></rss>";
        var handler = new StubHandler(HttpStatusCode.OK, "application/rss+xml", body);
        var probe = new HttpFeedReachabilityProbe(new HttpClient(handler));

        var result = await probe.CheckAsync("https://example.com/feed.xml", default);

        result.FailureClass.Should().Be(FeedPublishFailureClass.FeedNotParseable,
            because: "this is exactly the UTF-16-lies-about-UTF-8 bug class the probe must catch");
    }

    [Fact]
    public async Task Returns_FeedNotReachable_OnNetworkException()
    {
        var handler = new ThrowingHandler();
        var probe = new HttpFeedReachabilityProbe(new HttpClient(handler));

        var result = await probe.CheckAsync("https://example.com/feed.xml", default);

        result.FailureClass.Should().Be(FeedPublishFailureClass.FeedNotReachable);
        result.Diagnostic.Should().Contain("HTTP request failed");
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _contentType;
        private readonly string _body;

        public StubHandler(HttpStatusCode status, string contentType, string body)
        {
            _status = status;
            _contentType = contentType;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, System.Text.Encoding.UTF8, _contentType),
            };
            return Task.FromResult(response);
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            throw new HttpRequestException("Simulated DNS/network failure");
        }
    }
}
