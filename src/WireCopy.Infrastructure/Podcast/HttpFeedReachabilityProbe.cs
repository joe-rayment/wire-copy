// Licensed under the MIT License. See LICENSE in the repository root.

using System.Net;
using System.Xml;
using System.Xml.Linq;
using WireCopy.Domain.ValueObjects.Podcast;

namespace WireCopy.Infrastructure.Podcast;

/// <summary>
/// Default reachability probe — performs an unauthenticated <c>HTTP GET</c>
/// of the feed URL. Anything other than a parseable XML body with an
/// RSS/XML content-type is classified per
/// <see cref="FeedPublishFailureClass"/> so the result screen can render
/// targeted remediation (workspace-nb6b).
/// </summary>
internal sealed class HttpFeedReachabilityProbe : IFeedReachabilityProbe
{
    private static readonly HashSet<string> AcceptableContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/rss+xml",
        "application/xml",
        "text/xml",
        "application/atom+xml",
    };

    private readonly HttpClient _client;

    public HttpFeedReachabilityProbe()
        : this(new HttpClient { Timeout = TimeSpan.FromSeconds(15) })
    {
    }

    public HttpFeedReachabilityProbe(HttpClient client)
    {
        _client = client;
    }

    public async Task<FeedReachabilityResult> CheckAsync(string feedUrl, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(feedUrl);

        HttpResponseMessage response;
        try
        {
            response = await _client.GetAsync(feedUrl, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            return new FeedReachabilityResult
            {
                FailureClass = FeedPublishFailureClass.FeedNotReachable,
                Diagnostic = $"HTTP request failed: {ex.Message}",
            };
        }
        catch (TaskCanceledException ex) when (ex is not OperationCanceledException)
        {
            return new FeedReachabilityResult
            {
                FailureClass = FeedPublishFailureClass.FeedNotReachable,
                Diagnostic = $"Request timed out: {ex.Message}",
            };
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (!response.IsSuccessStatusCode)
            {
                // workspace-p1px: 403 specifically routes to BucketNotPublic so
                // the auto-remediation path can attempt to add the
                // allUsers:objectViewer binding before surfacing the failure.
                // Other non-2xx statuses (404, 502, etc.) stay on the generic
                // FeedNotReachable path that just tells the user to retry.
                if (response.StatusCode == HttpStatusCode.Forbidden)
                {
                    return new FeedReachabilityResult
                    {
                        FailureClass = FeedPublishFailureClass.BucketNotPublic,
                        Diagnostic = $"Anonymous HTTP GET returned {status} — bucket may not grant allUsers:objectViewer.",
                        HttpStatusCode = status,
                        ContentType = contentType,
                    };
                }

                return new FeedReachabilityResult
                {
                    FailureClass = FeedPublishFailureClass.FeedNotReachable,
                    Diagnostic = $"Anonymous HTTP GET returned {status}.",
                    HttpStatusCode = status,
                    ContentType = contentType,
                };
            }

            if (contentType is not null && !AcceptableContentTypes.Contains(contentType))
            {
                return new FeedReachabilityResult
                {
                    FailureClass = FeedPublishFailureClass.FeedNotParseable,
                    Diagnostic =
                        $"Feed responded with Content-Type '{contentType}' — expected application/rss+xml or similar.",
                    HttpStatusCode = status,
                    ContentType = contentType,
                };
            }

            byte[] bodyBytes;
            try
            {
                bodyBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new FeedReachabilityResult
                {
                    FailureClass = FeedPublishFailureClass.FeedNotReachable,
                    Diagnostic = $"Failed to read response body: {ex.Message}",
                    HttpStatusCode = status,
                    ContentType = contentType,
                };
            }

            // Parse the raw bytes via XmlReader so a lying encoding declaration
            // (e.g. utf-16 prolog over UTF-8 bytes — the workspace-jc2v bug)
            // surfaces here exactly as Apple Podcasts' anonymous fetcher would
            // see it. XDocument.Parse(string) would silently succeed because
            // the .NET string is already decoded by then.
            try
            {
                using var stream = new MemoryStream(bodyBytes);
                _ = XDocument.Load(stream);
            }
            catch (Exception ex) when (ex is XmlException or InvalidOperationException or System.Text.DecoderFallbackException)
            {
                var prefix = PreviewBytes(bodyBytes);
                return new FeedReachabilityResult
                {
                    FailureClass = FeedPublishFailureClass.FeedNotParseable,
                    Diagnostic = $"Feed body did not parse as XML: {ex.Message}. First 200 bytes: {prefix}",
                    HttpStatusCode = status,
                    ContentType = contentType,
                };
            }

            return FeedReachabilityResult.Ok();
        }
    }

    private static string PreviewBytes(byte[] bytes)
    {
        var max = Math.Min(bytes.Length, 200);
        try
        {
            return System.Text.Encoding.UTF8.GetString(bytes, 0, max);
        }
        catch
        {
            return $"({max} bytes; first byte = 0x{bytes[0]:x2})";
        }
    }
}
