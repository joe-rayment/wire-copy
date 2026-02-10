// <copyright file="NavigationContextTests.cs" company="NYT Audio Scraper">
// Educational and personal use only.
// </copyright>

using FluentAssertions;
using NYTAudioScraper.Domain.Entities.Browser;
using NYTAudioScraper.Domain.Enums.Browser;
using NYTAudioScraper.Domain.ValueObjects.Browser;
using Xunit;

namespace NYTAudioScraper.Tests.Browser;

public class NavigationContextTests
{
    [Fact]
    public void DefaultContext_HasCorrectDefaults()
    {
        // Arrange & Act
        var context = new NavigationContext();

        // Assert
        context.CurrentPage.Should().BeNull();
        context.ViewMode.Should().Be(ViewMode.Hierarchical);
        context.SelectedLinkIndex.Should().Be(0);
        context.ScrollOffset.Should().Be(0);
        context.BackHistoryCount.Should().Be(0);
        context.ForwardHistoryCount.Should().Be(0);
    }

    [Fact]
    public void HasReadableContent_WithPageWithContent_ReturnsTrue()
    {
        // Arrange
        var page = CreatePageWithReadableContent();
        var context = new NavigationContext { CurrentPage = page };

        // Act & Assert
        context.HasReadableContent.Should().BeTrue();
    }

    [Fact]
    public void HasReadableContent_WithPageWithoutContent_ReturnsFalse()
    {
        // Arrange
        var page = Page.Create(
            "https://example.com",
            "<html></html>",
            new PageMetadata { Title = "Test" });
        var context = new NavigationContext { CurrentPage = page };

        // Act & Assert
        context.HasReadableContent.Should().BeFalse();
    }

    [Fact]
    public void HasReadableContent_WithNullPage_ReturnsFalse()
    {
        // Arrange
        var context = new NavigationContext { CurrentPage = null };

        // Act & Assert
        context.HasReadableContent.Should().BeFalse();
    }

    [Fact]
    public void CanGoBack_WithBackHistory_ReturnsTrue()
    {
        // Arrange
        var context = new NavigationContext { BackHistoryCount = 3 };

        // Act & Assert
        context.CanGoBack.Should().BeTrue();
    }

    [Fact]
    public void CanGoBack_WithNoBackHistory_ReturnsFalse()
    {
        // Arrange
        var context = new NavigationContext { BackHistoryCount = 0 };

        // Act & Assert
        context.CanGoBack.Should().BeFalse();
    }

    [Fact]
    public void CanGoForward_WithForwardHistory_ReturnsTrue()
    {
        // Arrange
        var context = new NavigationContext { ForwardHistoryCount = 2 };

        // Act & Assert
        context.CanGoForward.Should().BeTrue();
    }

    [Fact]
    public void CanGoForward_WithNoForwardHistory_ReturnsFalse()
    {
        // Arrange
        var context = new NavigationContext { ForwardHistoryCount = 0 };

        // Act & Assert
        context.CanGoForward.Should().BeFalse();
    }

    [Fact]
    public void NavigationContext_IsRecord_SupportsWithExpression()
    {
        // Arrange
        var original = new NavigationContext
        {
            ViewMode = ViewMode.Hierarchical,
            SelectedLinkIndex = 0,
            BackHistoryCount = 5
        };

        // Act
        var modified = original with
        {
            ViewMode = ViewMode.Readable,
            SelectedLinkIndex = 3
        };

        // Assert
        modified.ViewMode.Should().Be(ViewMode.Readable);
        modified.SelectedLinkIndex.Should().Be(3);
        modified.BackHistoryCount.Should().Be(5); // Unchanged
    }

    [Fact]
    public void LoadedAt_DefaultsToCurrentTime()
    {
        // Arrange
        var before = DateTime.UtcNow;

        // Act
        var context = new NavigationContext();

        // Assert
        var after = DateTime.UtcNow;
        context.LoadedAt.Should().BeOnOrAfter(before);
        context.LoadedAt.Should().BeOnOrBefore(after);
    }

    [Fact]
    public void LoadedAt_CanBeOverridden()
    {
        // Arrange
        var specificTime = new DateTime(2024, 1, 22, 12, 0, 0, DateTimeKind.Utc);

        // Act
        var context = new NavigationContext { LoadedAt = specificTime };

        // Assert
        context.LoadedAt.Should().Be(specificTime);
    }

    [Theory]
    [InlineData(ViewMode.Hierarchical)]
    [InlineData(ViewMode.Readable)]
    public void ViewMode_AcceptsAllModes(ViewMode mode)
    {
        // Arrange & Act
        var context = new NavigationContext { ViewMode = mode };

        // Assert
        context.ViewMode.Should().Be(mode);
    }

    [Fact]
    public void NavigationContext_ValueEquality()
    {
        // Arrange
        var time = DateTime.UtcNow;
        var context1 = new NavigationContext
        {
            CurrentPage = null,
            ViewMode = ViewMode.Hierarchical,
            SelectedLinkIndex = 5,
            ScrollOffset = 10,
            BackHistoryCount = 2,
            ForwardHistoryCount = 1,
            LoadedAt = time
        };

        var context2 = new NavigationContext
        {
            CurrentPage = null,
            ViewMode = ViewMode.Hierarchical,
            SelectedLinkIndex = 5,
            ScrollOffset = 10,
            BackHistoryCount = 2,
            ForwardHistoryCount = 1,
            LoadedAt = time
        };

        // Assert
        context1.Should().Be(context2);
    }

    private static Page CreatePageWithReadableContent()
    {
        var page = Page.Create(
            "https://example.com/article",
            "<html><body>Article content</body></html>",
            new PageMetadata { Title = "Test Article" });

        page.SetReadableContent(ReadableContent.Create(
            "Test Article",
            "This is the article content.",
            new List<string> { "This is the article content." }));

        return page;
    }
}
