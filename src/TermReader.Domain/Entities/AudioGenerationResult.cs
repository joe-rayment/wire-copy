// <copyright file="AudioGenerationResult.cs" company="TermReader">
// Educational and personal use only.
// </copyright>


namespace TermReader.Domain.Entities;

/// <summary>
/// Represents the result of generating audio for multiple articles
/// </summary>
public class AudioGenerationResult
{
    /// <summary>
    /// Successfully generated audio files (article ID -> audio data)
    /// </summary>
    public required Dictionary<string, byte[]> SuccessfulGenerations { get; init; }

    /// <summary>
    /// Failed article generations (article ID -> error message)
    /// </summary>
    public required Dictionary<string, string> FailedGenerations { get; init; }

    /// <summary>
    /// Total number of articles processed
    /// </summary>
    public int TotalProcessed => SuccessfulGenerations.Count + FailedGenerations.Count;

    /// <summary>
    /// Number of successful generations
    /// </summary>
    public int SuccessCount => SuccessfulGenerations.Count;

    /// <summary>
    /// Number of failed generations
    /// </summary>
    public int FailureCount => FailedGenerations.Count;

    /// <summary>
    /// Whether all articles were successfully processed
    /// </summary>
    public bool AllSuccessful => FailedGenerations.Count == 0;

    /// <summary>
    /// Whether at least one article was successfully processed
    /// </summary>
    public bool AnySuccessful => SuccessfulGenerations.Count > 0;
}
