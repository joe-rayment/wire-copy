// <copyright file="PageTests.cs" company="TermReader">
// Educational and personal use only.
// </copyright>

using FluentAssertions;
using TermReader.Domain.Entities.Browser;
using TermReader.Domain.Enums.Browser;
using TermReader.Domain.ValueObjects.Browser;
using Xunit;

namespace TermReader.Tests.Browser;

[Trait("Category", "Unit")]
public class PageTests
{
    [Fact]
    public void Create_WithValidData_ReturnsPage()
    {
        // Arrange
        var url = "https://example.com/article";
        var html = "<html><body>Content</body></html>";
        var metadata = new PageMetadata { Title = "Test Article" };

        // Act
        var page = Page.Create(url, html, metadata);

        // Assert
        page.Should().NotBeNull();
        page.Url.Should().Be(url);
        page.RawHtml.Should().Be(html);
        page.Metadata.Should().Be(metadata);
        page.Id.Should().NotBeEmpty();
    }

    [Fact]
    public void Create_SetsLoadedAtToCurrentTime()
    {
        // Arrange
        var before = DateTime.UtcNow;
        var metadata = new PageMetadata { Title = "Test" };

        // Act
        var page = Page.Create("https://example.com", "<html></html>", metadata);
        var after = DateTime.UtcNow;

        // Assert
        page.LoadedAt.Should().BeOnOrAfter(before);
        page.LoadedAt.Should().BeOnOrBefore(after);
    }

    [Fact]
    public void Create_WithEmptyUrl_ThrowsArgumentException()
    {
        // Arrange
        var metadata = new PageMetadata { Title = "Test" };

        // Act & Assert
        var act = () => Page.Create("", "<html></html>", metadata);
        act.Should().Throw<ArgumentException>().WithParameterName("url");
    }

    [Fact]
    public void Create_WithWhitespaceUrl_ThrowsArgumentException()
    {
        // Arrange
        var metadata = new PageMetadata { Title = "Test" };

        // Act & Assert
        var act = () => Page.Create("   ", "<html></html>", metadata);
        act.Should().Throw<ArgumentException>().WithParameterName("url");
    }

    [Fact]
    public void Create_WithEmptyHtml_ThrowsArgumentException()
    {
        // Arrange
        var metadata = new PageMetadata { Title = "Test" };

        // Act & Assert
        var act = () => Page.Create("https://example.com", "", metadata);
        act.Should().Throw<ArgumentException>().WithParameterName("rawHtml");
    }

    [Fact]
    public void Create_WithNullMetadata_ThrowsArgumentNullException()
    {
        // Act & Assert
        var act = () => Page.Create("https://example.com", "<html></html>", null!);
        act.Should().Throw<ArgumentNullException>().WithParameterName("metadata");
    }

    [Fact]
    public void SetLinkTree_SetsNavigationTree()
    {
        // Arrange
        var page = CreateTestPage();
        var links = new List<LinkInfo>
        {
            new() { Url = "https://example.com/1", DisplayText = "Link 1", Type = LinkType.Content, ImportanceScore = 80 }
        };
        var tree = NavigationTree.Build(links);

        // Act
        page.SetLinkTree(tree);

        // Assert
        page.LinkTree.Should().Be(tree);
    }

    [Fact]
    public void SetLinkTree_WithNull_ThrowsArgumentNullException()
    {
        // Arrange
        var page = CreateTestPage();

        // Act & Assert
        var act = () => page.SetLinkTree(null!);
        act.Should().Throw<ArgumentNullException>().WithParameterName("tree");
    }

    [Fact]
    public void SetReadableContent_SetsContent()
    {
        // Arrange
        var page = CreateTestPage();
        var content = ReadableContent.Create("Title", "Content", new List<string> { "Content" });

        // Act
        page.SetReadableContent(content);

        // Assert
        page.ReadableContent.Should().Be(content);
    }

    [Fact]
    public void SetReadableContent_WithNull_ThrowsArgumentNullException()
    {
        // Arrange
        var page = CreateTestPage();

        // Act & Assert
        var act = () => page.SetReadableContent(null!);
        act.Should().Throw<ArgumentNullException>().WithParameterName("content");
    }

    [Fact]
    public void HasReadableContent_WithContent_ReturnsTrue()
    {
        // Arrange
        var page = CreateTestPage();
        page.SetReadableContent(ReadableContent.Create("Title", "Content", new List<string> { "Content" }));

        // Act & Assert
        page.HasReadableContent().Should().BeTrue();
    }

    [Fact]
    public void HasReadableContent_WithoutContent_ReturnsFalse()
    {
        // Arrange
        var page = CreateTestPage();

        // Act & Assert
        page.HasReadableContent().Should().BeFalse();
    }

    [Fact]
    public void HasLinks_WithLinks_ReturnsTrue()
    {
        // Arrange
        var page = CreateTestPage();
        var links = new List<LinkInfo>
        {
            new() { Url = "https://example.com/1", DisplayText = "Link 1", Type = LinkType.Content, ImportanceScore = 80 }
        };
        page.SetLinkTree(NavigationTree.Build(links));

        // Act & Assert
        page.HasLinks().Should().BeTrue();
    }

    [Fact]
    public void HasLinks_WithEmptyTree_ReturnsFalse()
    {
        // Arrange
        var page = CreateTestPage();
        page.SetLinkTree(NavigationTree.Build(new List<LinkInfo>()));

        // Act & Assert
        page.HasLinks().Should().BeFalse();
    }

    [Fact]
    public void HasLinks_WithoutTree_ReturnsFalse()
    {
        // Arrange
        var page = CreateTestPage();

        // Act & Assert
        page.HasLinks().Should().BeFalse();
    }

    [Fact]
    public void GetSummary_ReturnsFormattedString()
    {
        // Arrange
        var page = CreateTestPage();
        var links = new List<LinkInfo>
        {
            new() { Url = "https://example.com/1", DisplayText = "Link 1", Type = LinkType.Content, ImportanceScore = 80 },
            new() { Url = "https://example.com/2", DisplayText = "Link 2", Type = LinkType.Content, ImportanceScore = 80 }
        };
        page.SetLinkTree(NavigationTree.Build(links));
        page.SetReadableContent(ReadableContent.Create("Title", "Content", new List<string> { "Content" }));

        // Act
        var summary = page.GetSummary();

        // Assert
        summary.Should().Contain("Test Article"); // Title from metadata
        summary.Should().Contain("https://example.com/article"); // URL
        summary.Should().Contain("Links: 2"); // Link count
        summary.Should().Contain("Readable: Yes"); // Has readable content
    }

    [Fact]
    public void GetSummary_WithoutOptionalData_ShowsCorrectInfo()
    {
        // Arrange
        var page = CreateTestPage();

        // Act
        var summary = page.GetSummary();

        // Assert
        summary.Should().Contain("Links: 0");
        summary.Should().Contain("Readable: No");
    }

    [Fact]
    public void Page_HasUniqueId()
    {
        // Arrange
        var metadata = new PageMetadata { Title = "Test" };

        // Act
        var page1 = Page.Create("https://example.com/1", "<html></html>", metadata);
        var page2 = Page.Create("https://example.com/2", "<html></html>", metadata);

        // Assert
        page1.Id.Should().NotBe(page2.Id);
    }

    private static Page CreateTestPage()
    {
        return Page.Create(
            "https://example.com/article",
            "<html><body>Content</body></html>",
            new PageMetadata { Title = "Test Article" });
    }
}
