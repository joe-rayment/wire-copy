// <copyright file="IChapterMarker.cs" company="NYTAudioScraper">
// Copyright (c) NYTAudioScraper. All rights reserved.
// </copyright>

using NYTAudioScraper.Domain.Entities;

namespace NYTAudioScraper.Application.Interfaces;

/// <summary>
/// Service for adding chapter markers to audio files
/// </summary>
public interface IChapterMarker
{
    /// <summary>
    /// Adds chapter markers to an audiobook file
    /// </summary>
    /// <param name="audioFilePath">Path to the audio file</param>
    /// <param name="chapters">List of chapters to add</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task AddChaptersAsync(
        string audioFilePath,
        IEnumerable<AudioChapter> chapters,
        CancellationToken cancellationToken = default);
}
