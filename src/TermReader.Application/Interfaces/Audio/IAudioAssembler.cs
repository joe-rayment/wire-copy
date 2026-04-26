// Licensed under the MIT License. See LICENSE in the repository root.

using TermReader.Application.DTOs.Audio;

namespace TermReader.Application.Interfaces.Audio;

/// <summary>
/// Assembles multiple audio segments into a single M4B file with chapter markers.
/// </summary>
public interface IAudioAssembler
{
    /// <summary>
    /// Assembles audio segments into a single M4B file with embedded metadata and chapter markers.
    /// </summary>
    /// <param name="request">The assembly request containing segments, metadata, and output path.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The assembly result with output path and file information, or an error.</returns>
    Task<AssemblyResult> AssembleAsync(
        AssemblyRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates that required external tools (e.g., FFmpeg) are available.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if all prerequisites are satisfied.</returns>
    Task<bool> ValidatePrerequisitesAsync(CancellationToken cancellationToken = default);
}
