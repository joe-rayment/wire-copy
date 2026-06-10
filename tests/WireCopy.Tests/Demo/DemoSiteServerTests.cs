// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using WireCopy.Infrastructure.Demo;
using Xunit;

namespace WireCopy.Tests.Demo;

[Trait("Category", "Unit")]
public sealed class DemoSiteServerTests : IDisposable
{
    private readonly string _root;

    public DemoSiteServerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"wirecopy-demo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_root, "news"));
        File.WriteAllText(Path.Combine(_root, "index.html"), "<!DOCTYPE html><h1>The Daily Gazette</h1>");
        File.WriteAllText(Path.Combine(_root, "gazette.css"), "body{}");
        File.WriteAllText(Path.Combine(_root, "news", "story.html"), "<article>story</article>");
        File.WriteAllText(Path.Combine(Path.GetTempPath(), $"outside-{Path.GetFileName(_root)}.txt"), "secret");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [SkippableFact]
    public async Task ServesIndex_Css_NestedArticles_AndRefusesTraversal()
    {
        using var server = new DemoSiteServer(_root, NullLogger.Instance);
        try
        {
            server.Start();
        }
        catch (Exception ex)
        {
            Skip.If(true, $"Port {DemoSiteServer.Port} unavailable here: {ex.Message}");
            return;
        }

        using var http = new HttpClient { BaseAddress = new Uri(DemoSiteServer.Origin) };

        var index = await http.GetAsync("/");
        index.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        index.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
        (await index.Content.ReadAsStringAsync()).Should().Contain("Daily Gazette");

        var css = await http.GetAsync("/gazette.css");
        css.Content.Headers.ContentType!.MediaType.Should().Be("text/css");

        var story = await http.GetAsync("/news/story.html");
        story.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);

        (await http.GetAsync("/missing.html")).StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound);
        (await http.GetAsync("/..%2f..%2fetc%2fpasswd")).StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound);
    }

    [Fact]
    public void ResolveContentRoot_FindsRepoCheckoutPack()
    {
        // Running from the repo, demo/site exists relative to the test binary's
        // ancestors (the repo root) — the probe must find it.
        var root = DemoSiteServer.ResolveContentRoot();
        root.Should().NotBeNull();
        File.Exists(Path.Combine(root!, "index.html")).Should().BeTrue();
    }
}
