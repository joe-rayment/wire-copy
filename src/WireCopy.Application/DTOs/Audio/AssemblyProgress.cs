// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Application.DTOs.Audio;

/// <summary>
/// Per-step progress emitted by <see cref="WireCopy.Application.Interfaces.Audio.IAudioAssembler"/>
/// while it concatenates segments into a single M4B (workspace-74zy).
/// </summary>
public record AssemblyProgress
{
    /// <summary>
    /// Total number of audio segments queued for concatenation.
    /// </summary>
    public required int TotalSegments { get; init; }

    /// <summary>
    /// Number of segments concatenated so far (0..<see cref="TotalSegments"/>).
    /// </summary>
    public required int CompletedSegments { get; init; }

    /// <summary>
    /// FFmpeg's reported progress for the whole concat operation, 0–100.
    /// May be zero when the underlying library cannot estimate it. Useful
    /// for a smooth in-segment progress bar in addition to the discrete
    /// completed-segment counter.
    /// </summary>
    public double FfmpegPercent { get; init; }

    /// <summary>
    /// Human-readable status (e.g. "Concatenating 3 of 12 segments").
    /// </summary>
    public string? Message { get; init; }
}
