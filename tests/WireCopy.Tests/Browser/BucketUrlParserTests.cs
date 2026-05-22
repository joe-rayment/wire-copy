// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Infrastructure.Browser.CommandHandlers;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// Unit tests for <see cref="BucketUrlParser"/> (workspace-spue).
/// The Setup-screen bucket prompt accepts any of four forms; this class
/// asserts each one unwraps to the same bucket name.
/// </summary>
[Trait("Category", "Unit")]
public class BucketUrlParserTests
{
    [Theory]
    [InlineData("https://storage.googleapis.com/joe-podcast-feed/feed.xml", "joe-podcast-feed")]
    [InlineData("https://storage.googleapis.com/joe-podcast-feed/", "joe-podcast-feed")]
    [InlineData("https://storage.googleapis.com/joe-podcast-feed", "joe-podcast-feed")]
    [InlineData("https://storage.googleapis.com/joe-podcast-feed/podcasts/abc/feed.xml", "joe-podcast-feed")]
    public void ParseBucketName_PathStyleUrl_ReturnsBucket(string url, string expected)
    {
        BucketUrlParser.ParseBucketName(url).Should().Be(expected);
    }

    [Theory]
    [InlineData("https://joe-podcast-feed.storage.googleapis.com/feed.xml", "joe-podcast-feed")]
    [InlineData("https://joe-podcast-feed.storage.googleapis.com/", "joe-podcast-feed")]
    [InlineData("https://joe-podcast-feed.storage.googleapis.com", "joe-podcast-feed")]
    public void ParseBucketName_VirtualHostUrl_ReturnsBucket(string url, string expected)
    {
        BucketUrlParser.ParseBucketName(url).Should().Be(expected);
    }

    [Theory]
    [InlineData("gs://joe-podcast-feed/feed.xml", "joe-podcast-feed")]
    [InlineData("gs://joe-podcast-feed", "joe-podcast-feed")]
    [InlineData("gs://joe-podcast-feed/", "joe-podcast-feed")]
    [InlineData("GS://joe-podcast-feed/feed.xml", "joe-podcast-feed")]
    public void ParseBucketName_GsUrl_ReturnsBucket(string url, string expected)
    {
        BucketUrlParser.ParseBucketName(url).Should().Be(expected);
    }

    [Theory]
    [InlineData("joe-podcast-feed", "joe-podcast-feed")]
    [InlineData("  joe-podcast-feed  ", "joe-podcast-feed")]
    [InlineData("acmecorp-podcasts-prod", "acmecorp-podcasts-prod")]
    public void ParseBucketName_BareName_ReturnsTrimmed(string input, string expected)
    {
        BucketUrlParser.ParseBucketName(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ParseBucketName_Empty_ReturnsNull(string? input)
    {
        BucketUrlParser.ParseBucketName(input).Should().BeNull();
    }

    [Theory]
    [InlineData("mailto:joe@example.com")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.com/feed.xml")]
    public void ParseBucketName_NonHttpScheme_ReturnsNull(string input)
    {
        BucketUrlParser.ParseBucketName(input).Should().BeNull();
    }

    [Theory]
    [InlineData("https://example.com/some/path")]
    [InlineData("https://storage.googleapis.com/")]
    [InlineData("https://storage.googleapis.com")]
    public void ParseBucketName_UnknownUrlShape_ReturnsNull(string input)
    {
        BucketUrlParser.ParseBucketName(input).Should().BeNull();
    }

    [Theory]
    [InlineData("https://storage.googleapis.com/my-bucket/feed.xml?token=abc&signed=true", "my-bucket")]
    [InlineData("https://storage.googleapis.com/my-bucket/feed.xml#chapter-1", "my-bucket")]
    [InlineData("https://my-bucket.storage.googleapis.com/feed.xml?v=1", "my-bucket")]
    [InlineData("https://my-bucket.storage.googleapis.com/feed.xml#anchor", "my-bucket")]
    public void ParseBucketName_QueryStringOrFragment_StillExtractsBucket(string url, string expected)
    {
        // Query strings and fragments are not part of the bucket identity — strip them.
        // Uri.AbsolutePath does this automatically, but we lock in the behavior so
        // a future refactor away from Uri.TryCreate doesn't silently regress.
        BucketUrlParser.ParseBucketName(url).Should().Be(expected);
    }

    [Theory]
    [InlineData("https://storage.googleapis.com/My-Bucket/feed.xml", "My-Bucket")]
    [InlineData("gs://Mixed-Case/", "Mixed-Case")]
    public void ParseBucketName_MixedCase_PassesThroughForValidatorToReject(string url, string expected)
    {
        // The parser preserves case so GcsConfiguration.ExplainBucketInvalid (which
        // requires all-lowercase) can return a precise error. Lowercasing here would
        // mask the actual user input from the validator's error message.
        BucketUrlParser.ParseBucketName(url).Should().Be(expected);
    }

    [Theory]
    [InlineData("https://example.com/feed", true)]
    [InlineData("http://example.com/feed", true)]
    [InlineData("gs://my-bucket", true)]
    [InlineData("HTTPS://example.com", true)]
    [InlineData("my-bucket", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void LooksLikeUrl_DistinguishesUrlFromBare(string? input, bool expected)
    {
        BucketUrlParser.LooksLikeUrl(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("joe-podcast-feed", "https://storage.googleapis.com/joe-podcast-feed/feed.xml")]
    [InlineData("acme", "https://storage.googleapis.com/acme/feed.xml")]
    [InlineData("  trimmed  ", "https://storage.googleapis.com/trimmed/feed.xml")]
    public void BuildFeedUrl_ReturnsCanonicalUrl(string bucket, string expected)
    {
        BucketUrlParser.BuildFeedUrl(bucket).Should().Be(expected);
    }

    [Fact]
    public void BuildFeedUrl_EmptyOrNull_ReturnsNull()
    {
        BucketUrlParser.BuildFeedUrl(null).Should().BeNull();
        BucketUrlParser.BuildFeedUrl("").Should().BeNull();
        BucketUrlParser.BuildFeedUrl("   ").Should().BeNull();
    }

    [Fact]
    public void ParseBucketName_RoundTripsWithBuildFeedUrl()
    {
        const string bucket = "joe-podcast-feed";

        var url = BucketUrlParser.BuildFeedUrl(bucket);
        var roundTripped = BucketUrlParser.ParseBucketName(url);

        roundTripped.Should().Be(bucket);
    }
}
