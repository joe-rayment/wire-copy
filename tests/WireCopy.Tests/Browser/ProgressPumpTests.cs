// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Infrastructure.Browser;
using Xunit;

namespace WireCopy.Tests.Browser;

[Trait("Category", "Unit")]
public class ProgressPumpTests
{
    [Fact]
    public async Task RunAsync_SlowWork_TicksWhilePending_ThenStops()
    {
        var ticks = 0;
        var work = Task.Run(async () => { await Task.Delay(600); return 42; });

        var result = await ProgressPump.RunAsync(
            work,
            _ => { Interlocked.Increment(ref ticks); return Task.CompletedTask; },
            TimeSpan.FromMilliseconds(100));

        result.Should().Be(42);
        ticks.Should().BeGreaterThanOrEqualTo(2, "a >500ms call repaints the indicator several times");

        var ticksAtReturn = ticks;
        await Task.Delay(300);
        ticks.Should().Be(ticksAtReturn, "the pump must not tick after the work completes (no leak)");
    }

    [Fact]
    public async Task RunAsync_AlreadyComplete_NeverTicks()
    {
        var ticks = 0;
        var result = await ProgressPump.RunAsync(
            Task.FromResult("done"),
            _ => { ticks++; return Task.CompletedTask; },
            TimeSpan.FromMilliseconds(50));

        result.Should().Be("done");
        ticks.Should().Be(0);
    }
}
