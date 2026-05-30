// Licensed under the MIT License. See LICENSE in the repository root.

using System.Diagnostics;
using FluentAssertions;
using WireCopy.Infrastructure.Browser;
using Xunit;

namespace WireCopy.Tests.Browser;

[Trait("Category", "Unit")]
public class ScreenshotCaptureTests
{
    [Fact]
    public async Task WithCapAsync_FastCapture_ReturnsBytes_AndInvokesCaptureOnce()
    {
        var calls = 0;
        var bytes = await ScreenshotCapture.WithCapAsync(
            () => { calls++; return Task.FromResult<byte[]?>(new byte[] { 1, 2, 3 }); },
            TimeSpan.FromSeconds(5));

        bytes.Should().NotBeNull().And.HaveCount(3);
        calls.Should().Be(1, "the AI path captures the screenshot exactly once");
    }

    [Fact]
    public async Task WithCapAsync_SlowCapture_ExceedsCap_ReturnsNullPromptly()
    {
        var sw = Stopwatch.StartNew();
        var bytes = await ScreenshotCapture.WithCapAsync(
            async () => { await Task.Delay(3000); return new byte[] { 9 }; },
            TimeSpan.FromMilliseconds(100));
        sw.Stop();

        bytes.Should().BeNull("past the cap the analyzer proceeds text-only");
        sw.ElapsedMilliseconds.Should().BeLessThan(2000, "we must not block on the slow capture");
    }

    [Fact]
    public async Task WithCapAsync_CaptureThrows_ReturnsNull()
    {
        var bytes = await ScreenshotCapture.WithCapAsync(
            () => throw new InvalidOperationException("no session"),
            TimeSpan.FromSeconds(1));

        bytes.Should().BeNull();
    }
}
