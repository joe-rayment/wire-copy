// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Application.Interfaces;

/// <summary>Severity of a captured log entry (provider-agnostic).</summary>
public enum LogSeverity
{
    Trace,
    Debug,
    Information,
    Warning,
    Error,
    Critical,
}

/// <summary>
/// workspace-v3pz: one captured log entry, structured so the in-app log viewer
/// can colour/filter by level and search the message without re-parsing the
/// rolling log file. Kept provider-agnostic (no Serilog / MS.Logging types) so
/// the viewer depends only on the Application layer.
/// </summary>
public sealed record LogRecord
{
    public required DateTimeOffset Timestamp { get; init; }

    public required LogSeverity Level { get; init; }

    public required string Message { get; init; }

    /// <summary>Exception detail (ToString), or null.</summary>
    public string? Exception { get; init; }

    /// <summary>The Serilog SourceContext (logger category), or null.</summary>
    public string? SourceContext { get; init; }
}
