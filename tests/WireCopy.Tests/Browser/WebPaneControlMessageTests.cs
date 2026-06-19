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
        using var doc = JsonDocument.Parse(WebPaneHostBridge.BuildModeMessage(WebPaneMode.Live));
        var root = doc.RootElement;

        root.GetProperty("kind").GetString().Should().Be("mode");
        root.GetProperty("mode").GetString().Should().Be("live");
        // workspace-8a5y: Snapshot mode is retired — no control message ever carries reader HTML.
        root.TryGetProperty("html", out _).Should().BeFalse();
    }

    [Fact]
    public void Hidden_BuildsModeHiddenMessage()
    {
        using var doc = JsonDocument.Parse(WebPaneHostBridge.BuildModeMessage(WebPaneMode.Hidden));
        var root = doc.RootElement;

        root.GetProperty("kind").GetString().Should().Be("mode");
        root.GetProperty("mode").GetString().Should().Be("hidden");
        root.TryGetProperty("html", out _).Should().BeFalse();
    }

    [Fact]
    public void Toggle_BuildsToggleMessage()
    {
        using var doc = JsonDocument.Parse(WebPaneHostBridge.BuildToggleMessage());

        doc.RootElement.GetProperty("kind").GetString().Should().Be("toggle");
    }
}
