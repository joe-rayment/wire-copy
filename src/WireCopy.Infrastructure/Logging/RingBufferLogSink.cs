// Licensed under the MIT License. See LICENSE in the repository root.

using Serilog.Core;
using Serilog.Events;
using WireCopy.Application.Interfaces;

namespace WireCopy.Infrastructure.Logging;

/// <summary>
/// workspace-v3pz: a Serilog sink that keeps the most recent log entries in a
/// bounded in-memory ring, exposed as <see cref="ILogBuffer"/> for the in-app
/// <c>:logs</c> viewer. Thread-safe; a single instance is shared between the
/// Serilog pipeline (<c>WriteTo.Sink</c>) and DI so the viewer reads exactly what
/// was logged.
/// </summary>
public sealed class RingBufferLogSink : ILogEventSink, ILogBuffer
{
    private readonly int _capacity;
    private readonly Queue<LogRecord> _buffer;
    private readonly object _gate = new();

    public RingBufferLogSink(int capacity = 2000)
    {
        _capacity = Math.Max(1, capacity);
        _buffer = new Queue<LogRecord>(_capacity);
    }

    /// <inheritdoc />
    public int Capacity => _capacity;

    /// <inheritdoc />
    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);

        var sourceContext = logEvent.Properties.TryGetValue("SourceContext", out var sc)
            ? sc.ToString().Trim('"')
            : null;

        var record = new LogRecord
        {
            Timestamp = logEvent.Timestamp,
            Level = MapLevel(logEvent.Level),
            Message = logEvent.RenderMessage(),
            Exception = logEvent.Exception?.ToString(),
            SourceContext = sourceContext,
        };

        lock (_gate)
        {
            _buffer.Enqueue(record);
            while (_buffer.Count > _capacity)
            {
                _buffer.Dequeue();
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<LogRecord> Snapshot()
    {
        lock (_gate)
        {
            return _buffer.ToArray();
        }
    }

    private static LogSeverity MapLevel(LogEventLevel level) => level switch
    {
        LogEventLevel.Verbose => LogSeverity.Trace,
        LogEventLevel.Debug => LogSeverity.Debug,
        LogEventLevel.Information => LogSeverity.Information,
        LogEventLevel.Warning => LogSeverity.Warning,
        LogEventLevel.Error => LogSeverity.Error,
        LogEventLevel.Fatal => LogSeverity.Critical,
        _ => LogSeverity.Information,
    };
}
