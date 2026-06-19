// <copyright file="WebPaneControlMessageTests.cs" company="Wire Copy">
// Licensed under the MIT License. See LICENSE in the repository root.
// </copyright>

using System.Text.Json;
using FluentAssertions;
using WireCopy.Infrastructure.Browser;
using Xunit;

namespace WireCopy.Tests.Browser;

[Trait("Category", "Unit")]
public class WebPaneControlMessageTests
{
    [Fact]
    public void Live_BuildsModeLiveMessage()
    {
        using var doc = JsonDocument.Parse(WebPaneHostBridge.BuildModeMessage(WebPaneMode.Live, null));
        var root = doc.RootElement;

        root.GetProperty("kind").GetString().Should().Be("mode");
        root.GetProperty("mode").GetString().Should().Be("live");
        root.TryGetProperty("html", out _).Should().BeFalse();
    }

    [Fact]
    public void Hidden_BuildsModeHiddenMessage()
    {
        using var doc = JsonDocument.Parse(WebPaneHostBridge.BuildModeMessage(WebPaneMode.Hidden, "ignored"));
        var root = doc.RootElement;

        root.GetProperty("kind").GetString().Should().Be("mode");
        root.GetProperty("mode").GetString().Should().Be("hidden");
        root.TryGetProperty("html", out _).Should().BeFalse();
    }

    [Fact]
    public void Snapshot_CarriesTheHtmlPayload()
    {
        var html = "<h1>Title</h1><p>Body</p>";

        using var doc = JsonDocument.Parse(WebPaneHostBridge.BuildModeMessage(WebPaneMode.Snapshot, html));
        var root = doc.RootElement;

        root.GetProperty("kind").GetString().Should().Be("mode");
        root.GetProperty("mode").GetString().Should().Be("snapshot");
        root.GetProperty("html").GetString().Should().Be(html);
    }

    [Fact]
    public void Snapshot_NullHtml_BecomesEmptyString()
    {
        using var doc = JsonDocument.Parse(WebPaneHostBridge.BuildModeMessage(WebPaneMode.Snapshot, null));

        doc.RootElement.GetProperty("html").GetString().Should().BeEmpty();
    }

    [Fact]
    public void Toggle_BuildsToggleMessage()
    {
        using var doc = JsonDocument.Parse(WebPaneHostBridge.BuildToggleMessage());

        doc.RootElement.GetProperty("kind").GetString().Should().Be("toggle");
    }
}
