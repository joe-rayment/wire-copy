// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Infrastructure.Browser.Extension;

namespace WireCopy.Tests.Browser;

/// <summary>
/// The "click in place vs. reload the tab" rule for the host-browser-as-renderer model
/// (workspace-blg5.1): a same-document fragment link is an in-page jump the real browser handles
/// natively, so the orchestrator clicks the anchor instead of re-navigating (which would reload the
/// whole document and restart the overlay).
/// </summary>
public sealed class ExtensionNavigationTests
{
    [Theory]
    [InlineData("https://en.wikipedia.org/wiki/Cloudflare", "https://en.wikipedia.org/wiki/Cloudflare#History")]
    [InlineData("https://site.test/a?x=1", "https://site.test/a?x=1#sec")]
    [InlineData("http://host/page", "http://host/page#top")]
    public void IsSameDocumentFragment_TrueForFragmentOfCurrentPage(string current, string target)
    {
        ExtensionNavigation.IsSameDocumentFragment(current, target).Should().BeTrue();
    }

    [Theory]
    // Different path → a real navigation, not an in-page jump.
    [InlineData("https://en.wikipedia.org/wiki/Cloudflare", "https://en.wikipedia.org/wiki/Akamai#History")]
    // No fragment at all → ordinary navigation.
    [InlineData("https://site.test/a", "https://site.test/a")]
    // Empty/bare fragment → not a jump target.
    [InlineData("https://site.test/a", "https://site.test/a#")]
    // Different query → different document state.
    [InlineData("https://site.test/a?x=1", "https://site.test/a?x=2#sec")]
    // Different host → real navigation.
    [InlineData("https://a.test/p", "https://b.test/p#sec")]
    public void IsSameDocumentFragment_FalseOtherwise(string current, string target)
    {
        ExtensionNavigation.IsSameDocumentFragment(current, target).Should().BeFalse();
    }

    [Theory]
    [InlineData(null, "https://site.test/a#sec")]
    [InlineData("https://site.test/a", null)]
    [InlineData("not a url", "also not a url#x")]
    public void IsSameDocumentFragment_FalseForNullOrUnparseable(string? current, string? target)
    {
        ExtensionNavigation.IsSameDocumentFragment(current, target).Should().BeFalse();
    }
}
