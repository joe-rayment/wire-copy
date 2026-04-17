// Educational and personal use only.

using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TermReader.Domain.Entities.Browser;
using TermReader.Domain.Enums.Browser;
using TermReader.Domain.ValueObjects.Browser;
using TermReader.Infrastructure.Browser;
using Xunit;

namespace TermReader.Tests.Browser;

[Trait("Category", "Unit")]
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

    [Fact]
    public void GoBack_ResetsViewModeToHierarchical()
    {
        // Arrange
        var page1 = CreateTestPage("https://example.com/1", "Page 1");
        var page2 = CreateTestPage("https://example.com/2", "Page 2");
        _sut.NavigateTo(page1);
        _sut.NavigateTo(page2);
        _sut.SetViewMode(ViewMode.Readable);

        // Act
        _sut.GoBack();

        // Assert
        _sut.CurrentContext.ViewMode.Should().Be(ViewMode.Hierarchical);
    }

    [Fact]
    public void GoForward_ResetsViewModeToHierarchical()
    {
        // Arrange
        var page1 = CreateTestPage("https://example.com/1", "Page 1");
        var page2 = CreateTestPage("https://example.com/2", "Page 2");
        _sut.NavigateTo(page1);
        _sut.NavigateTo(page2);
        _sut.SetViewMode(ViewMode.Readable);
        _sut.GoBack();
        _sut.SetViewMode(ViewMode.Readable);

        // Act
        _sut.GoForward();

        // Assert
        _sut.CurrentContext.ViewMode.Should().Be(ViewMode.Hierarchical);
    }

    // Speed reading tests

    [Fact]
    public void SpeedRead_DefaultWpm_Is250()
    {
        _sut.CurrentContext.SpeedReadWpm.Should().Be(250);
    }

    [Fact]
    public void SpeedRead_DefaultState_IsInactive()
    {
        _sut.CurrentContext.IsSpeedReadActive.Should().BeFalse();
    }

    [Fact]
    public void StartSpeedRead_SetsActive()
    {
        _sut.StartSpeedRead();

        _sut.IsSpeedReadActive.Should().BeTrue();
        _sut.CurrentContext.IsSpeedReadActive.Should().BeTrue();
    }

    [Fact]
    public void StopSpeedRead_ClearsActive()
    {
        _sut.StartSpeedRead();
        _sut.StopSpeedRead();

        _sut.IsSpeedReadActive.Should().BeFalse();
        _sut.CurrentContext.IsSpeedReadActive.Should().BeFalse();
    }

    [Fact]
    public void AdjustSpeedReadWpm_IncreasesWpm()
    {
        _sut.AdjustSpeedReadWpm(50);

        _sut.SpeedReadWpm.Should().Be(300);
        _sut.CurrentContext.SpeedReadWpm.Should().Be(300);
    }

    [Fact]
    public void AdjustSpeedReadWpm_DecreasesWpm()
    {
        _sut.AdjustSpeedReadWpm(-50);

        _sut.SpeedReadWpm.Should().Be(200);
    }

    [Fact]
    public void AdjustSpeedReadWpm_ClampsAtMinimum50()
    {
        _sut.AdjustSpeedReadWpm(-500);

        _sut.SpeedReadWpm.Should().Be(50);
    }

    [Fact]
    public void AdjustSpeedReadWpm_ClampsAtMaximum1000()
    {
        _sut.AdjustSpeedReadWpm(1000);

        _sut.SpeedReadWpm.Should().Be(1000);
    }

    [Fact]
    public void NavigateTo_StopsSpeedRead()
    {
        _sut.StartSpeedRead();

        _sut.NavigateTo(CreateTestPage());

        _sut.IsSpeedReadActive.Should().BeFalse();
    }

    [Fact]
    public void GoBack_StopsSpeedRead()
    {
        var page1 = CreateTestPage("https://example.com/1", "Page 1");
        var page2 = CreateTestPage("https://example.com/2", "Page 2");
        _sut.NavigateTo(page1);
        _sut.NavigateTo(page2);
        _sut.StartSpeedRead();

        _sut.GoBack();

        _sut.IsSpeedReadActive.Should().BeFalse();
    }

    [Fact]
    public void GoForward_StopsSpeedRead()
    {
        var page1 = CreateTestPage("https://example.com/1", "Page 1");
        var page2 = CreateTestPage("https://example.com/2", "Page 2");
        _sut.NavigateTo(page1);
        _sut.NavigateTo(page2);
        _sut.GoBack();
        _sut.StartSpeedRead();

        _sut.GoForward();

        _sut.IsSpeedReadActive.Should().BeFalse();
    }

    [Fact]
    public void SetViewMode_StopsSpeedRead()
    {
        _sut.StartSpeedRead();

        _sut.SetViewMode(ViewMode.Readable);

        _sut.IsSpeedReadActive.Should().BeFalse();
    }

    [Fact]
    public void ToggleViewMode_StopsSpeedRead()
    {
        _sut.StartSpeedRead();

        _sut.ToggleViewMode();

        _sut.IsSpeedReadActive.Should().BeFalse();
    }

    [Fact]
    public void ClearHistory_StopsSpeedRead()
    {
        _sut.StartSpeedRead();

        _sut.ClearHistory();

        _sut.IsSpeedReadActive.Should().BeFalse();
    }
}
