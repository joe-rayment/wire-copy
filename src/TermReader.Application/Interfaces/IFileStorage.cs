// <copyright file="IFileStorage.cs" company="TermReader">
// Educational and personal use only.
// </copyright>


namespace TermReader.Application.Interfaces;

/// <summary>
/// Service for file storage operations
/// </summary>
public interface IFileStorage
{
    /// <summary>
    /// Saves audio data to a file
    /// </summary>
    /// <param name="audioData">Audio data bytes</param>
    /// <param name="fileName">File name</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Path to saved file</returns>
    Task<string> SaveAudioAsync(
        byte[] audioData,
        string fileName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the output directory path
    /// </summary>
    string GetOutputDirectory();

    /// <summary>
    /// Deletes a file
    /// </summary>
    Task DeleteFileAsync(string filePath, CancellationToken cancellationToken = default);
}
