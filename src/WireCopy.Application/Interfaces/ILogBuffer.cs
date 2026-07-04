// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Application.Interfaces;

/// <summary>
/// workspace-v3pz: a bounded, in-memory ring of the most recent log entries the
/// process has emitted, so the user can pull them up inside the app (the log
/// file is the only sink in browse mode — nothing reaches the terminal). Fed by
/// a Serilog sink; read by the <c>:logs</c> viewer.
/// </summary>
public interface ILogBuffer
{
    /// <summary>Max entries retained; older entries are evicted.</summary>
    int Capacity { get; }

    /// <summary>A point-in-time copy of the buffered entries, oldest first.</summary>
    IReadOnlyList<LogRecord> Snapshot();
}
