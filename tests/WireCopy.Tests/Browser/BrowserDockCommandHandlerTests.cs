// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using NSubstitute;
using WireCopy.Infrastructure.Browser;
using WireCopy.Infrastructure.Browser.CommandHandlers;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// Unit tests for the toggle-vs-summon decision in <see cref="BrowserDockCommandHandler"/>
/// (lens-on-demand, workspace-ziky). Drives a fake <see cref="IBrowserSession"/> so the
/// decision logic is exercised without a real browser.
/// </summary>
public class BrowserDockCommandHandlerTests
{
    [Fact]
    public async Task ResolveAsync_NullSession_ReturnsNull()
    {
        var state = await BrowserDockCommandHandler.ResolveAsync(null, "https://example.com");
        state.Should().BeNull();
    }

    [Fact]
    public async Task ResolveAsync_TogglesExistingDockedWindow_DoesNotSummon()
    {
        var session = Substitute.For<IBrowserSession>();
        session.ToggleWindowDockAsync().Returns(BrowserWindowState.Docked);

        var summonInvoked = false;
        var state = await BrowserDockCommandHandler.ResolveAsync(
            session,
            "https://example.com",
            onSummoning: () => { summonInvoked = true; return Task.CompletedTask; });

        state.Should().Be(BrowserWindowState.Docked);
        summonInvoked.Should().BeFalse("a headed window already existed, so no summon is needed");
        await session.DidNotReceive().SummonAndDockAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task ResolveAsync_TogglesExistingWindowToMinimized_DoesNotSummon()
    {
        var session = Substitute.For<IBrowserSession>();
        session.ToggleWindowDockAsync().Returns(BrowserWindowState.Minimized);

        var state = await BrowserDockCommandHandler.ResolveAsync(session, "https://example.com");

        state.Should().Be(BrowserWindowState.Minimized);
        await session.DidNotReceive().SummonAndDockAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task ResolveAsync_NoHeadedWindowButLiveUrl_SummonsAndDocks()
    {
        var session = Substitute.For<IBrowserSession>();
        session.ToggleWindowDockAsync().Returns((BrowserWindowState?)null);
        session.SummonAndDockAsync("https://nytimes.com/article").Returns(BrowserWindowState.Docked);

        var summonInvoked = false;
        var state = await BrowserDockCommandHandler.ResolveAsync(
            session,
            "https://nytimes.com/article",
            onSummoning: () => { summonInvoked = true; return Task.CompletedTask; });

        state.Should().Be(BrowserWindowState.Docked);
        summonInvoked.Should().BeTrue("the slow summon path should paint an 'opening…' status first");
        await session.Received(1).SummonAndDockAsync("https://nytimes.com/article");
    }

    [Fact]
    public async Task ResolveAsync_NoHeadedWindowAndNoUrl_ReturnsNullWithoutSummoning()
    {
        var session = Substitute.For<IBrowserSession>();
        session.ToggleWindowDockAsync().Returns((BrowserWindowState?)null);

        var summonInvoked = false;
        var state = await BrowserDockCommandHandler.ResolveAsync(
            session,
            currentUrl: null,
            onSummoning: () => { summonInvoked = true; return Task.CompletedTask; });

        state.Should().BeNull();
        summonInvoked.Should().BeFalse();
        await session.DidNotReceive().SummonAndDockAsync(Arg.Any<string>());
    }

    [Theory]
    [InlineData("data:text/html,<title>x</title>")]
    [InlineData("about:blank")]
    [InlineData("file:///tmp/page.html")]
    [InlineData("not a url")]
    public async Task ResolveAsync_NonHttpUrl_DoesNotSummon(string url)
    {
        var session = Substitute.For<IBrowserSession>();
        session.ToggleWindowDockAsync().Returns((BrowserWindowState?)null);

        var state = await BrowserDockCommandHandler.ResolveAsync(session, url);

        state.Should().BeNull();
        await session.DidNotReceive().SummonAndDockAsync(Arg.Any<string>());
    }

    [Theory]
    [InlineData("https://example.com", true)]
    [InlineData("http://example.com/path?q=1", true)]
    [InlineData("HTTPS://EXAMPLE.COM", true)]
    [InlineData("data:text/html,x", false)]
    [InlineData("about:blank", false)]
    [InlineData("file:///x", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void IsSummonableUrl_ClassifiesUrls(string? url, bool expected)
    {
        BrowserDockCommandHandler.IsSummonableUrl(url).Should().Be(expected);
    }
}
