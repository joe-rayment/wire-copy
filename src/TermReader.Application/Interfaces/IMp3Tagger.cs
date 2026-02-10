// <copyright file="IMp3Tagger.cs" company="TermReader">
// Educational and personal use only.
// </copyright>

namespace TermReader.Application.Interfaces;

/// <summary>
/// Service for adding ID3 metadata tags to MP3 files.
/// </summary>
public interface IMp3Tagger
{
    /// <summary>
    /// Tags an MP3 file with the specified metadata.
    /// </summary>
    /// <param name="filePath">Path to the MP3 file.</param>
    /// <param name="metadata">Metadata to apply to the file.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task TagFileAsync(string filePath, Mp3Metadata metadata);
}

/// <summary>
/// Metadata for an MP3 file.
/// </summary>
/// <param name="Title">Title of the track (e.g., "Headline - Nov 27, 2025").</param>
/// <param name="Artist">Artist name (e.g., "The New York Times").</param>
/// <param name="Album">Album name (e.g., "NYT Today's Paper").</param>
/// <param name="TrackNumber">Track number in the album (1, 2, 3...).</param>
/// <param name="PublishDate">Original publish date of the article.</param>
public record Mp3Metadata(
    string Title,
    string Artist,
    string Album,
    int TrackNumber,
    DateTime PublishDate);
