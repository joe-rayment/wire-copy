// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Serilog.Events;
using Serilog.Parsing;
using WireCopy.Application.Interfaces;
using WireCopy.Infrastructure.Logging;
using Xunit;

namespace WireCopy.Tests.Logging;

public class RingBufferLogSinkTests
{
    private static LogEvent Event(string message, LogEventLevel level = LogEventLevel.Information, Exception? ex = null) =>
        new(
            DateTimeOffset.UtcNow,
            level,
            ex,
            new MessageTemplateParser().Parse(message),
            Array.Empty<LogEventProperty>());

    [Fact]
    public void RecordsInOrder_AndMapsFields()
    {
        var sink = new RingBufferLogSink(10);
        sink.Emit(Event("first"));
        sink.Emit(Event("second", LogEventLevel.Warning, new InvalidOperationException("boom")));

        var snap = sink.Snapshot();
        snap.Should().HaveCount(2);
        snap[0].Message.Should().Be("first");
        snap[0].Level.Should().Be(LogSeverity.Information);
        snap[1].Message.Should().Be("second");
        snap[1].Level.Should().Be(LogSeverity.Warning);
        snap[1].Exception.Should().Contain("boom");
    }

    [Fact]
    public void EvictsOldestPastCapacity()
    {
        var sink = new RingBufferLogSink(3);
        for (var i = 1; i <= 5; i++)
        {
            sink.Emit(Event($"m{i}"));
        }

        var snap = sink.Snapshot();
        snap.Should().HaveCount(3);
        snap.Select(r => r.Message).Should().Equal("m3", "m4", "m5");
        sink.Capacity.Should().Be(3);
    }

    [Fact]
    public void Snapshot_IsAStableCopy()
    {
        var sink = new RingBufferLogSink(10);
        sink.Emit(Event("a"));
        var snap = sink.Snapshot();
        sink.Emit(Event("b"));
        snap.Should().HaveCount(1, "an earlier snapshot must not see later emits");
    }

    [Fact]
    public void ThreadSafe_UnderConcurrentEmit()
    {
        var sink = new RingBufferLogSink(5000);
        Parallel.For(0, 2000, i => sink.Emit(Event($"m{i}")));
        sink.Snapshot().Count.Should().Be(2000);
    }
}
