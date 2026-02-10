// <copyright file="LinkInfoTests.cs" company="NYT Audio Scraper">
// Educational and personal use only.
// </copyright>

using FluentAssertions;
using NYTAudioScraper.Domain.Enums.Browser;
using NYTAudioScraper.Domain.ValueObjects.Browser;
using Xunit;

namespace NYTAudioScraper.Tests.Browser;

public class LinkInfoTests
{
    [Theory]
    [InlineData(LinkType.Content, 80, false)]   // High importance content - expanded
    [InlineData(LinkType.Content, 50, false)]   // Threshold content - expanded
    [InlineData(LinkType.Content, 49, true)]    // Below threshold content - collapsed
    [InlineData(LinkType.Content, 30, true)]    // Low importance content - collapsed
    [InlineData(LinkType.Navigation, 80, true)] // Navigation always collapsed
    [InlineData(LinkType.Navigation, 30, true)] // Navigation always collapsed
    [InlineData(LinkType.Footer, 80, true)]     // Footer always collapsed
    [InlineData(LinkType.Footer, 10, true)]     // Footer always collapsed
    [InlineData(LinkType.External, 80, true)]   // External always collapsed
    [InlineData(LinkType.External, 10, true)]   // External always collapsed
    public void ShouldStartCollapsed_BasedOnTypeAndImportance(LinkType type, int importance, bool expectedCollapsed)
    {
        // Arrange
        var linkInfo = new LinkInfo
        {
            Url = "https://example.com",
            DisplayText = "Test Link",
            Type = type,
            ImportanceScore = importance
        };

        // Act
        var result = linkInfo.ShouldStartCollapsed();

        // Assert
        result.Should().Be(expectedCollapsed);
    }

    [Fact]
    public void LinkInfo_IsRecord_SupportsValueEquality()
    {
        // Arrange
        var link1 = new LinkInfo
        {
            Url = "https://example.com",
            DisplayText = "Test",
            Type = LinkType.Content,
            ImportanceScore = 80
        };

        var link2 = new LinkInfo
        {
            Url = "https://example.com",
            DisplayText = "Test",
            Type = LinkType.Content,
            ImportanceScore = 80
        };

        // Assert
        link1.Should().Be(link2);
        (link1 == link2).Should().BeTrue();
    }

    [Fact]
    public void LinkInfo_DifferentValues_NotEqual()
    {
        // Arrange
        var link1 = new LinkInfo
        {
            Url = "https://example.com/1",
            DisplayText = "Link 1",
            Type = LinkType.Content,
            ImportanceScore = 80
        };

        var link2 = new LinkInfo
        {
            Url = "https://example.com/2",
            DisplayText = "Link 2",
            Type = LinkType.Navigation,
            ImportanceScore = 30
        };

        // Assert
        link1.Should().NotBe(link2);
    }

    [Fact]
    public void LinkInfo_OptionalAriaLabel_CanBeNull()
    {
        // Arrange
        var link = new LinkInfo
        {
            Url = "https://example.com",
            DisplayText = "Test",
            Type = LinkType.Content,
            ImportanceScore = 80,
            AriaLabel = null
        };

        // Assert
        link.AriaLabel.Should().BeNull();
    }

    [Fact]
    public void LinkInfo_OptionalAriaLabel_CanBeSet()
    {
        // Arrange
        var link = new LinkInfo
        {
            Url = "https://example.com",
            DisplayText = "Click here",
            Type = LinkType.Content,
            ImportanceScore = 80,
            AriaLabel = "Read more about the article"
        };

        // Assert
        link.AriaLabel.Should().Be("Read more about the article");
    }

    [Fact]
    public void LinkInfo_OptionalParentSelector_CanBeNull()
    {
        // Arrange
        var link = new LinkInfo
        {
            Url = "https://example.com",
            DisplayText = "Test",
            Type = LinkType.Content,
            ImportanceScore = 80,
            ParentSelector = null
        };

        // Assert
        link.ParentSelector.Should().BeNull();
    }

    [Fact]
    public void LinkInfo_OptionalParentSelector_CanBeSet()
    {
        // Arrange
        var link = new LinkInfo
        {
            Url = "https://example.com",
            DisplayText = "Test",
            Type = LinkType.Content,
            ImportanceScore = 80,
            ParentSelector = "nav > ul > li"
        };

        // Assert
        link.ParentSelector.Should().Be("nav > ul > li");
    }

    [Fact]
    public void LinkInfo_SupportsWithExpression()
    {
        // Arrange
        var original = new LinkInfo
        {
            Url = "https://example.com",
            DisplayText = "Original",
            Type = LinkType.Content,
            ImportanceScore = 80
        };

        // Act
        var modified = original with { DisplayText = "Modified" };

        // Assert
        modified.Url.Should().Be(original.Url);
        modified.DisplayText.Should().Be("Modified");
        modified.Type.Should().Be(original.Type);
        modified.ImportanceScore.Should().Be(original.ImportanceScore);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(50)]
    [InlineData(100)]
    public void ImportanceScore_AcceptsValidRange(int score)
    {
        // Arrange & Act
        var link = new LinkInfo
        {
            Url = "https://example.com",
            DisplayText = "Test",
            Type = LinkType.Content,
            ImportanceScore = score
        };

        // Assert
        link.ImportanceScore.Should().Be(score);
    }
}
