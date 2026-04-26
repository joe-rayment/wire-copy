// <copyright file="IFileStorage.cs" company="TermReader">
// Licensed under the MIT License. See LICENSE in the repository root.
// </copyright>


namespace TermReader.Application.Interfaces;

/// <summary>
/// Service for file storage operations
/// </summary>
public interface IFileStorage
{
    /// <summary>
    /// Gets the output directory path
    /// </summary>
    string GetOutputDirectory();

    /// <summary>
    /// Deletes a file
    /// </summary>
    Task DeleteFileAsync(string filePath, CancellationToken cancellationToken = default);
}
