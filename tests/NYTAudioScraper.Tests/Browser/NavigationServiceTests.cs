// Educational and personal use only.

using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NYTAudioScraper.Domain.Entities.Browser;
using NYTAudioScraper.Domain.Enums.Browser;
using NYTAudioScraper.Domain.ValueObjects.Browser;
using NYTAudioScraper.Infrastructure.Browser;
using Xunit;

namespace NYTAudioScraper.Tests.Browser;

public class NavigationServiceTests
{
    private readonly NavigationService _sut;
    private readonly ILogger<NavigationService> _logger;

    public NavigationServiceTests()
    {
        _logger = Substitute.For<ILogger<NavigationService>>();
        _sut = new NavigationService(_logger);
    }

    private static Page CreateTestPage(string url = "https://example.com", string title = "Test Page")
    {
        var metadata = new PageMetadata { Title = title };
        return Page.Create(url, "<html><body>Test</body></html>", metadata);
    }

    [Fact]
    public void NavigateTo_ResetsScrollOffsetToZero()
    {
        // Arrange
        var page1 = CreateTestPage("https://example.com/1", "Page 1");
        _sut.NavigateTo(page1);
        _sut.SetScrollOffset(50);

        // Act
        var page2 = CreateTestPage("https://example.com/2", "Page 2");
        _sut.NavigateTo(page2);

        // Assert
        _sut.CurrentContext.ScrollOffset.Should().Be(0);
    }

    [Fact]
    public void NavigateTo_ResetsViewModeToHierarchical()
    {
        // Arrange
        var page1 = CreateTestPage("https://example.com/1", "Page 1");
        _sut.NavigateTo(page1);
        _sut.SetViewMode(ViewMode.Readable);

        // Act
        var page2 = CreateTestPage("https://example.com/2", "Page 2");
        _sut.NavigateTo(page2);

        // Assert
        _sut.CurrentContext.ViewMode.Should().Be(ViewMode.Hierarchical);
    }

    [Fact]
    public void NavigateTo_PushesCurrentPageToBackHistory()
    {
        // Arrange
        var page1 = CreateTestPage("https://example.com/1", "Page 1");
        _sut.NavigateTo(page1);

        // Act
        var page2 = CreateTestPage("https://example.com/2", "Page 2");
        _sut.NavigateTo(page2);

        // Assert
        _sut.CanGoBack.Should().BeTrue();
        _sut.BackHistoryCount.Should().Be(1);
    }

    [Fact]
    public void GoBack_ResetsScrollOffsetToZero()
    {
        // Arrange
        var page1 = CreateTestPage("https://example.com/1", "Page 1");
        var page2 = CreateTestPage("https://example.com/2", "Page 2");
        _sut.NavigateTo(page1);
        _sut.NavigateTo(page2);
        _sut.SetScrollOffset(30);

        // Act
        _sut.GoBack();

        // Assert
        _sut.CurrentContext.ScrollOffset.Should().Be(0);
    }

    [Fact]
    public void GoBack_PushesCurrentPageToForwardHistory()
    {
        // Arrange
        var page1 = CreateTestPage("https://example.com/1", "Page 1");
        var page2 = CreateTestPage("https://example.com/2", "Page 2");
        _sut.NavigateTo(page1);
        _sut.NavigateTo(page2);

        // Act
        _sut.GoBack();

        // Assert
        _sut.CanGoForward.Should().BeTrue();
        _sut.ForwardHistoryCount.Should().Be(1);
    }

    [Fact]
    public void GoForward_ResetsScrollOffsetToZero()
    {
        // Arrange
        var page1 = CreateTestPage("https://example.com/1", "Page 1");
        var page2 = CreateTestPage("https://example.com/2", "Page 2");
        _sut.NavigateTo(page1);
        _sut.NavigateTo(page2);
        _sut.GoBack();
        _sut.SetScrollOffset(25);

        // Act
        _sut.GoForward();

        // Assert
        _sut.CurrentContext.ScrollOffset.Should().Be(0);
    }

    [Fact]
    public void GoForward_PushesCurrentPageToBackHistory()
    {
        // Arrange
        var page1 = CreateTestPage("https://example.com/1", "Page 1");
        var page2 = CreateTestPage("https://example.com/2", "Page 2");
        _sut.NavigateTo(page1);
        _sut.NavigateTo(page2);
        _sut.GoBack();

        // Act
        _sut.GoForward();

        // Assert
        _sut.CanGoBack.Should().BeTrue();
        _sut.BackHistoryCount.Should().Be(1);
    }

    [Fact]
    public void SetViewMode_ResetsScrollOffset()
    {
        // Arrange
        var page = CreateTestPage();
        _sut.NavigateTo(page);
        _sut.SetScrollOffset(42);

        // Act
        _sut.SetViewMode(ViewMode.Readable);

        // Assert
        _sut.CurrentContext.ScrollOffset.Should().Be(0);
        _sut.CurrentContext.ViewMode.Should().Be(ViewMode.Readable);
    }

    [Fact]
    public void ToggleViewMode_ResetsScrollOffset()
    {
        // Arrange
        var page = CreateTestPage();
        _sut.NavigateTo(page);
        _sut.SetScrollOffset(15);

        // Act
        _sut.ToggleViewMode();

        // Assert
        _sut.CurrentContext.ScrollOffset.Should().Be(0);
    }

    [Fact]
    public void ToggleViewMode_SwitchesBetweenModes()
    {
        // Arrange
        var page = CreateTestPage();
        _sut.NavigateTo(page);
        _sut.CurrentContext.ViewMode.Should().Be(ViewMode.Hierarchical);

        // Act - toggle to Readable
        _sut.ToggleViewMode();
        _sut.CurrentContext.ViewMode.Should().Be(ViewMode.Readable);

        // Act - toggle back to Hierarchical
        _sut.ToggleViewMode();
        _sut.CurrentContext.ViewMode.Should().Be(ViewMode.Hierarchical);
    }

    [Fact]
    public void ClearHistory_ResetsAllState()
    {
        // Arrange
        var page1 = CreateTestPage("https://example.com/1", "Page 1");
        var page2 = CreateTestPage("https://example.com/2", "Page 2");
        var page3 = CreateTestPage("https://example.com/3", "Page 3");
        _sut.NavigateTo(page1);
        _sut.NavigateTo(page2);
        _sut.NavigateTo(page3);
        _sut.GoBack();
        _sut.SetScrollOffset(20);
        _sut.SetViewMode(ViewMode.Readable);

        // Act
        _sut.ClearHistory();

        // Assert
        _sut.CurrentPage.Should().BeNull();
        _sut.CanGoBack.Should().BeFalse();
        _sut.CanGoForward.Should().BeFalse();
        _sut.BackHistoryCount.Should().Be(0);
        _sut.ForwardHistoryCount.Should().Be(0);
        _sut.CurrentContext.ScrollOffset.Should().Be(0);
        _sut.CurrentContext.SelectedLinkIndex.Should().Be(0);
    }
}
