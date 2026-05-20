// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Application.DTOs.Audio;

namespace WireCopy.Application.Interfaces.Audio;

/// <summary>
/// Assembles multiple audio segments into a single M4B file with chapter markers.
/// </summary>
public interface IAudioAssembler
{
    /// <summary>
    /// Assembles audio segments into a single M4B file with embedded metadata and chapter markers.
    /// </summary>
    /// <param name="request">The assembly request containing segments, metadata, and output path.</param>
    /// <param name="progress">
    /// Optional sink for per-step progress. Wired into FFmpeg's
    /// <c>NotifyOnProgress</c> callback plus a tick per segment so the caller
    /// can show "N of M concatenated" copy and a smooth percent bar during
    /// the assembly phase (workspace-74zy).
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The assembly result with output path and file information, or an error.</returns>
    Task<AssemblyResult> AssembleAsync(
        AssemblyRequest request,
        IProgress<AssemblyProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates that required external tools (e.g., FFmpeg) are available.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if all prerequisites are satisfied.</returns>
    Task<bool> ValidatePrerequisitesAsync(CancellationToken cancellationToken = default);
}
