// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Infrastructure.BrowserExtension;
using Xunit;

namespace WireCopy.Tests.BrowserExtension;

[Trait("Category", "Unit")]
public sealed class BrowserExtensionLauncherTests
{
    // Guards:   The URL the browser is launched on is a file: URL to the guide page whose query carries the
    //           browser name and extension folder, and a folder with spaces, '&', '#', '%' and unicode in
    //           it survives the round trip exactly (the page reads it back with URLSearchParams).
    // Fails if: The URL is not file:, the query is not percent-escaped (a '&' or '#' in the path would cut
    //           the folder short), or unicode / spaces come back changed.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter BuildGuideUrl_RoundTripsAwkwardFolder
    [Theory]
    [InlineData("/home/jo/.local/share/WireCopy/browser-extension/add-extension.html", "/home/jo/.local/share/WireCopy/browser-extension/WireCopy-Extension", "Chromium")]
    [InlineData("/home/zoë o'brien/WireCopy/browser-extension/add-extension.html", "/home/zoë o'brien/WireCopy/browser-extension/WireCopy-Extension", "Vivaldi")]
    [InlineData("/data/a b/R&D #1 100%/WireCopy/browser-extension/add-extension.html", "/data/a b/R&D #1 100%/WireCopy/browser-extension/WireCopy-Extension", "Chrome")]
    [InlineData("/home/日本語/ф/add-extension.html", "/home/日本語/ф/WireCopy-Extension", "Edge")]
    public void BuildGuideUrl_RoundTripsAwkwardFolder(string guidePath, string folder, string browserName)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var url = BrowserExtensionLauncher.BuildGuideUrl(guidePath, browserName, folder);

        url.Should().StartWith("file:///");
        url.Should().NotContain(" ");
        var uri = new Uri(url);
        uri.IsFile.Should().BeTrue();
        uri.LocalPath.Should().Be(guidePath);

        var query = uri.Query.TrimStart('?').Split('&')
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(kv => kv[0], kv => Uri.UnescapeDataString(kv[1]));
        query.Keys.Should().BeEquivalentTo("browser", "folder");
        query["browser"].Should().Be(browserName);
        query["folder"].Should().Be(folder);
    }
}
