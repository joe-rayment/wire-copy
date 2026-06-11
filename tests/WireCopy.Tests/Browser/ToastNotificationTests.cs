// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Browser;
using Xunit;

namespace WireCopy.Tests.Browser;

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
    public void MarkToastRendered_NonSticky_SurvivesRapidRerenders_UntilTtl()
    {
        // workspace-wef6.6: dismissal is clock-based. The old render-count
        // rule killed any toast on the 2nd render pass, so a quick re-render
        // (progress tick, animation frame) ate it before a human could read it.
        var (sut, clock) = CreateNavigationServiceWithClock();

        sut.ShowToast(ToastType.Info, "Cache warmed");

        // Dozens of rapid render passes inside the TTL window keep the toast.
        for (var i = 0; i < 40; i++)
        {
            sut.MarkToastRendered();
            clock.Advance(TimeSpan.FromMilliseconds(50));
            sut.CurrentContext.ActiveToast.Should().NotBeNull(
                $"render pass #{i} at {(i + 1) * 50}ms is inside the 4s TTL");
        }

        // Past the TTL, the next render pass dismisses it.
        clock.Advance(TimeSpan.FromSeconds(3));
        sut.MarkToastRendered();
        sut.CurrentContext.ActiveToast.Should().BeNull("the clock-based TTL elapsed");
    }

    [Fact]
    public void MarkToastRendered_TtlStartsAtFirstRender_NotAtShow()
    {
        var (sut, clock) = CreateNavigationServiceWithClock();
        sut.ShowToast(ToastType.Info, "Cache warmed");

        // Time passes before anything renders — the clock hasn't started.
        clock.Advance(TimeSpan.FromSeconds(10));
        sut.MarkToastRendered();
        sut.CurrentContext.ActiveToast.Should().NotBeNull(
            "the TTL is measured from the first render, not from ShowToast");

        clock.Advance(TimeSpan.FromSeconds(4.5));
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
    }

    private static NavigationService CreateNavigationService()
    {
        var logger = Substitute.For<ILogger<NavigationService>>();
        return new NavigationService(logger);
    }

    private static (NavigationService Service, FakeTimeProvider Clock) CreateNavigationServiceWithClock()
    {
        var clock = new FakeTimeProvider(new DateTime(2026, 6, 11, 12, 0, 0, DateTimeKind.Utc));
        var logger = Substitute.For<ILogger<NavigationService>>();
        return (new NavigationService(logger, clock), clock);
    }
}
