// Educational and personal use only.

using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TermReader.Domain.Enums.Browser;
using TermReader.Domain.ValueObjects.Browser;
using TermReader.Infrastructure.Browser;
using Xunit;

namespace TermReader.Tests.Browser;

/// <summary>
/// Tests for toast notification domain model and lifecycle management in NavigationService.
/// </summary>
[Trait("Category", "Unit")]
public class ToastNotificationTests
{
    [Theory]
    [InlineData(ToastType.Error)]
    [InlineData(ToastType.Celebration)]
    public void IsSticky_ReturnsTrue_ForErrorAndCelebration(ToastType type)
    {
        var toast = new ToastNotification { Type = type, Message = "test" };
        toast.IsSticky.Should().BeTrue();
    }

    [Theory]
    [InlineData(ToastType.Info)]
    [InlineData(ToastType.Success)]
    public void IsSticky_ReturnsFalse_ForInfoAndSuccess(ToastType type)
    {
        var toast = new ToastNotification { Type = type, Message = "test" };
        toast.IsSticky.Should().BeFalse();
    }

    [Fact]
    public void HasBeenRendered_DefaultsFalse()
    {
        var toast = new ToastNotification { Type = ToastType.Info, Message = "test" };
        toast.HasBeenRendered.Should().BeFalse();
    }

    [Fact]
    public void Detail_IsOptional()
    {
        var toast = new ToastNotification { Type = ToastType.Success, Message = "Saved" };
        toast.Detail.Should().BeNull();

        var toastWithDetail = new ToastNotification
        {
            Type = ToastType.Success,
            Message = "Saved",
            Detail = "to Reading List"
        };
        toastWithDetail.Detail.Should().Be("to Reading List");
    }

    [Fact]
    public void ShowToast_SetsActiveToast()
    {
        var sut = CreateNavigationService();

        sut.ShowToast(ToastType.Info, "Cache warmed", "24/24");

        var context = sut.CurrentContext;
        context.ActiveToast.Should().NotBeNull();
        context.ActiveToast!.Type.Should().Be(ToastType.Info);
        context.ActiveToast.Message.Should().Be("Cache warmed");
        context.ActiveToast.Detail.Should().Be("24/24");
    }

    [Fact]
    public void ShowToast_ReplacesExistingToast()
    {
        var sut = CreateNavigationService();

        sut.ShowToast(ToastType.Info, "First toast");
        sut.ShowToast(ToastType.Error, "Second toast");

        var context = sut.CurrentContext;
        context.ActiveToast.Should().NotBeNull();
        context.ActiveToast!.Message.Should().Be("Second toast");
        context.ActiveToast.Type.Should().Be(ToastType.Error);
    }

    [Fact]
    public void ClearToast_RemovesActiveToast()
    {
        var sut = CreateNavigationService();

        sut.ShowToast(ToastType.Success, "Done");
        sut.ClearToast();

        sut.CurrentContext.ActiveToast.Should().BeNull();
    }

    [Fact]
    public void MarkToastRendered_NonSticky_ClearsAfterSecondCall()
    {
        var sut = CreateNavigationService();

        // Show a non-sticky (Info) toast
        sut.ShowToast(ToastType.Info, "Cache warmed");

        // First call: marks as rendered but keeps the toast
        sut.MarkToastRendered();
        sut.CurrentContext.ActiveToast.Should().NotBeNull();
        sut.CurrentContext.ActiveToast!.HasBeenRendered.Should().BeTrue();

        // Second call: auto-dismisses because it was already rendered
        sut.MarkToastRendered();
        sut.CurrentContext.ActiveToast.Should().BeNull();
    }

    [Fact]
    public void MarkToastRendered_Sticky_PersistsAcrossMultipleCalls()
    {
        var sut = CreateNavigationService();

        // Show a sticky (Error) toast
        sut.ShowToast(ToastType.Error, "Network timeout");

        // First call: marks as rendered
        sut.MarkToastRendered();
        sut.CurrentContext.ActiveToast.Should().NotBeNull();

        // Second call: sticky toast persists
        sut.MarkToastRendered();
        sut.CurrentContext.ActiveToast.Should().NotBeNull();

        // Third call: still persists
        sut.MarkToastRendered();
        sut.CurrentContext.ActiveToast.Should().NotBeNull();
        sut.CurrentContext.ActiveToast!.Message.Should().Be("Network timeout");
    }

    [Fact]
    public void MarkToastRendered_CelebrationSticky_PersistsAcrossMultipleCalls()
    {
        var sut = CreateNavigationService();

        sut.ShowToast(ToastType.Celebration, "Podcast ready!", "12 chapters");

        sut.MarkToastRendered();
        sut.MarkToastRendered();
        sut.MarkToastRendered();

        var toast = sut.CurrentContext.ActiveToast;
        toast.Should().NotBeNull();
        toast!.Type.Should().Be(ToastType.Celebration);
        toast.Detail.Should().Be("12 chapters");
    }

    [Fact]
    public void MarkToastRendered_NoToast_DoesNothing()
    {
        var sut = CreateNavigationService();

        // Should not throw when there's no toast
        sut.MarkToastRendered();
        sut.CurrentContext.ActiveToast.Should().BeNull();
    }

    [Fact]
    public void ClearToast_AfterStickyToast_Clears()
    {
        var sut = CreateNavigationService();

        sut.ShowToast(ToastType.Error, "Error");
        sut.MarkToastRendered();
        sut.MarkToastRendered();

        // Even sticky toasts can be manually cleared
        sut.ClearToast();
        sut.CurrentContext.ActiveToast.Should().BeNull();
    }

    [Fact]
    public void ShowToast_WithoutDetail_SetsDetailToNull()
    {
        var sut = CreateNavigationService();

        sut.ShowToast(ToastType.Success, "Bookmark added");

        var toast = sut.CurrentContext.ActiveToast;
        toast.Should().NotBeNull();
        toast!.Detail.Should().BeNull();
        toast.HasBeenRendered.Should().BeFalse();
    }

    private static NavigationService CreateNavigationService()
    {
        var logger = Substitute.For<ILogger<NavigationService>>();
        return new NavigationService(logger);
    }
}
