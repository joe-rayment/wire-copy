// <copyright file="WebPaneDecisionTests.cs" company="Wire Copy">
// Licensed under the MIT License. See LICENSE in the repository root.
// </copyright>

using FluentAssertions;
using WireCopy.Domain.Entities.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Browser;
using Xunit;

namespace WireCopy.Tests.Browser;

[Trait("Category", "Unit")]
public class WebPaneDecisionTests
{
    private static Page WebPage(string url = "https://example.com/page")
        => Page.Create(url, "<html></html>", new PageMetadata { Title = "T" });

    private static Page ArticlePage(string url = "https://example.com/article")
    {
        var page = WebPage(url);
        page.SetReadableContent(ReadableContent.Create("Title", "body text", new List<string> { "body text" }));
        return page;
    }

    [Fact]
    public void NullPage_IsHidden()
        => WebPaneDecision.Decide(ViewMode.Hierarchical, null).Should().Be(WebPaneMode.Hidden);

    [Fact]
    public void Launcher_IsHidden()
        => WebPaneDecision.Decide(ViewMode.Launcher, WebPage()).Should().Be(WebPaneMode.Hidden);

    [Fact]
    public void CollectionList_IsHidden()
        => WebPaneDecision.Decide(ViewMode.CollectionList, WebPage()).Should().Be(WebPaneMode.Hidden);

    [Fact]
    public void HierarchicalLinkList_IsLive()
        => WebPaneDecision.Decide(ViewMode.Hierarchical, WebPage()).Should().Be(WebPaneMode.Live);

    [Fact]
    public void ReaderWithArticle_IsSnapshot()
        => WebPaneDecision.Decide(ViewMode.Readable, ArticlePage()).Should().Be(WebPaneMode.Snapshot);

    [Fact]
    public void ReaderWithoutExtractedContent_FallsBackToLive()
        => WebPaneDecision.Decide(ViewMode.Readable, WebPage()).Should().Be(WebPaneMode.Live);

    [Fact]
    public void NonSummonableUrl_IsHidden()
        => WebPaneDecision.Decide(ViewMode.Hierarchical, WebPage("data:text/html,hi")).Should().Be(WebPaneMode.Hidden);
}
