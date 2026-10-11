// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Infrastructure.Browser.CommandHandlers;
using WireCopy.Infrastructure.BrowserExtension;
using Xunit;

namespace WireCopy.Tests.BrowserExtension;

[Trait("Category", "Unit")]
public sealed class BrowserExtensionCardTests
{
    private static readonly DateTime Now = new(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);

    private static BrowserRegistration Browser(string id, string name, bool detected = true) =>
        new(id, name, detected, [], detected ? "/usr/bin/" + id : null);

    private static BrowserConnection SeenAgo(TimeSpan ago) => new(Now - ago, "1.0.0");

    // Guards:   a browser that said hello shows "connected" with how long ago, and one that has not shows "ready to add".
    // Fails if: StatusText drops the relative time, or marks a never-seen browser as connected.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter StatusText_ConnectedVsReadyToAdd
    [Fact]
    public void StatusText_ConnectedVsReadyToAdd()
    {
        BrowserExtensionCard.StatusText(SeenAgo(TimeSpan.FromMinutes(2)), Now)
            .Should().Be("✓ connected · last used 2m ago");
        BrowserExtensionCard.StatusText(SeenAgo(TimeSpan.FromSeconds(5)), Now)
            .Should().Be("✓ connected · last used just now");
        BrowserExtensionCard.StatusText(null, Now).Should().Be("ready to add");
    }

    // Guards:   the Settings row reads "Not set up" until a browser connects, then names the connected browser.
    // Fails if: RowSummary reports connected with no connections, or shows the long display name instead of "Chrome".
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter RowSummary_NotSetUpThenOneBrowser
    [Fact]
    public void RowSummary_NotSetUpThenOneBrowser()
    {
        var registrations = new[] { Browser("chrome", "Google Chrome"), Browser("vivaldi", "Vivaldi") };

        BrowserExtensionCard.RowSummary(registrations, new Dictionary<string, BrowserConnection>())
            .Should().Be("Not set up");

        var vivaldi = new Dictionary<string, BrowserConnection> { ["vivaldi"] = SeenAgo(TimeSpan.FromMinutes(1)) };
        BrowserExtensionCard.RowSummary(registrations, vivaldi).Should().Be("Connected · Vivaldi");

        var chrome = new Dictionary<string, BrowserConnection> { ["chrome"] = SeenAgo(TimeSpan.FromMinutes(1)) };
        BrowserExtensionCard.RowSummary(registrations, chrome).Should().Be("Connected · Chrome");
    }

    // Guards:   with two connected browsers the row lists both, the most recently used first.
    // Fails if: only one browser is shown, or the order ignores which was seen last.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter RowSummary_TwoBrowsers
    [Fact]
    public void RowSummary_TwoBrowsers_MostRecentFirst()
    {
        var registrations = new[] { Browser("chrome", "Google Chrome"), Browser("vivaldi", "Vivaldi") };
        var connections = new Dictionary<string, BrowserConnection>
        {
            ["chrome"] = SeenAgo(TimeSpan.FromMinutes(1)),
            ["vivaldi"] = SeenAgo(TimeSpan.FromHours(3)),
        };

        BrowserExtensionCard.RowSummary(registrations, connections).Should().Be("Connected · Chrome, Vivaldi");
    }

    // Guards:   a "browser" entry (launcher could not name the browser) counts as the first detected browser being connected.
    // Fails if: the generic key is ignored, or it is applied to a browser that is not detected.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter ResolveConnections_GenericBrowserKey
    [Fact]
    public void ResolveConnections_GenericBrowserKey_MarksFirstDetectedBrowser()
    {
        var registrations = new[]
        {
            Browser("brave", "Brave", detected: false),
            Browser("chrome", "Google Chrome"),
            Browser("vivaldi", "Vivaldi"),
        };
        var connections = new Dictionary<string, BrowserConnection> { ["browser"] = SeenAgo(TimeSpan.FromMinutes(4)) };

        var resolved = BrowserExtensionCard.ResolveConnections(registrations, connections);

        resolved.Keys.Should().Equal("chrome");
        BrowserExtensionCard.RowSummary(registrations, connections).Should().Be("Connected · Chrome");
    }

    // Guards:   the card redraws exactly when something visible changes (a browser flips to connected).
    // Fails if: the state key ignores connections (card never updates) or changes every call (flicker).
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter BuildStateKey_ChangesOnlyWhenVisible
    [Fact]
    public void BuildStateKey_ChangesOnlyWhenVisible()
    {
        var registrations = new[] { Browser("vivaldi", "Vivaldi") };
        var none = new Dictionary<string, BrowserConnection>();
        var seen = new Dictionary<string, BrowserConnection> { ["vivaldi"] = new(Now, "1.0.0") };

        var before = BrowserExtensionCard.BuildStateKey(registrations, none, Now, 0, null, 80, 24);
        BrowserExtensionCard.BuildStateKey(registrations, none, Now.AddSeconds(30), 0, null, 80, 24)
            .Should().Be(before);
        BrowserExtensionCard.BuildStateKey(registrations, seen, Now, 0, null, 80, 24)
            .Should().NotBe(before);
    }
}
