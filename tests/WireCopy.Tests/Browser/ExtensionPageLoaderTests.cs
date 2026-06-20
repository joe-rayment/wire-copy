// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Infrastructure.Browser.Extension;

namespace WireCopy.Tests.Browser;

/// <summary>
/// Verifies the extension page-loader (workspace-wrs5) sources rendered DOM from the extension bridge
/// and runs the unchanged metadata extraction over it — and that it surfaces an actionable failure
/// when the extension is not attached.
/// </summary>
public sealed class ExtensionPageLoaderTests
{
    [Fact]
    public async Task LoadAsync_ReturnsExtensionDom_AndExtractsMetadata()
    {
        var bridge = Substitute.For<IExtensionBridge>();
        bridge.IsConnected.Returns(true);
        bridge.NavigateAndCaptureAsync("https://example.com/world", Arg.Any<CancellationToken>())
            .Returns(new ExtensionDomSnapshot(
                "https://example.com/world",
                "<html><head><title>World News</title></head><body><a href=\"/a\">A story headline that is long</a></body></html>",
                414,
                896));

        var loader = new ExtensionPageLoader(bridge, NullLogger<ExtensionPageLoader>.Instance);

        var result = await loader.LoadAsync(new PageLoadRequest { Url = "https://example.com/world" });

        result.Success.Should().BeTrue();
        result.Url.Should().Be("https://example.com/world");
        result.Html.Should().Contain("A story headline");
        result.Metadata.Should().NotBeNull();
        result.Metadata!.Title.Should().Be("World News");
    }

    [Fact]
    public async Task LoadAsync_WhenExtensionNeverConnects_ReturnsActionableFailure()
    {
        var bridge = Substitute.For<IExtensionBridge>();
        bridge.IsConnected.Returns(false);
        bridge.WaitForReadyAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(false);

        var loader = new ExtensionPageLoader(bridge, NullLogger<ExtensionPageLoader>.Instance);

        var result = await loader.LoadAsync(new PageLoadRequest { Url = "https://example.com" });

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("extension");
        await bridge.DidNotReceive().NavigateAndCaptureAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LoadAsync_WhenTabAlreadyOnUrl_CapturesInPlace_DoesNotRenavigate()
    {
        var bridge = Substitute.For<IExtensionBridge>();
        bridge.IsConnected.Returns(true);
        bridge.CurrentUrl.Returns("https://example.com/world/");
        bridge.CaptureDomAsync(Arg.Any<CancellationToken>())
            .Returns(new ExtensionDomSnapshot(
                "https://example.com/world",
                "<html><head><title>World</title></head><body><a href=\"/a\">A long story headline here</a></body></html>",
                1280, 800));

        var loader = new ExtensionPageLoader(bridge, NullLogger<ExtensionPageLoader>.Instance);

        // Same path, only trailing slash / scheme-case differs — must capture, not navigate.
        var result = await loader.LoadAsync(new PageLoadRequest { Url = "https://example.com/world" });

        result.Success.Should().BeTrue();
        await bridge.Received(1).CaptureDomAsync(Arg.Any<CancellationToken>());
        await bridge.DidNotReceive().NavigateAndCaptureAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void UrlsEquivalent_IgnoresTrailingSlashAndFragment_ButNotPath()
    {
        WireCopy.Infrastructure.Browser.Extension.ExtensionPageLoader
            .UrlsEquivalent("https://x.com/a", "https://x.com/a/").Should().BeTrue();
        WireCopy.Infrastructure.Browser.Extension.ExtensionPageLoader
            .UrlsEquivalent("https://x.com/a#frag", "https://x.com/a").Should().BeTrue();
        WireCopy.Infrastructure.Browser.Extension.ExtensionPageLoader
            .UrlsEquivalent("https://x.com/a", "https://x.com/b").Should().BeFalse();
        WireCopy.Infrastructure.Browser.Extension.ExtensionPageLoader
            .UrlsEquivalent("https://x.com/a", "").Should().BeFalse();
    }

    [Fact]
    public async Task LoadAsync_WhenDomEmpty_Fails()
    {
        var bridge = Substitute.For<IExtensionBridge>();
        bridge.IsConnected.Returns(true);
        bridge.NavigateAndCaptureAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ExtensionDomSnapshot("https://example.com", string.Empty, 0, 0));

        var loader = new ExtensionPageLoader(bridge, NullLogger<ExtensionPageLoader>.Instance);

        var result = await loader.LoadAsync(new PageLoadRequest { Url = "https://example.com" });

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("empty");
    }
}
