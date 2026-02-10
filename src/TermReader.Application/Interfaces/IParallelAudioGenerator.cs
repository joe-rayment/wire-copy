// <copyright file="IParallelAudioGenerator.cs" company="TermReader">
// Educational and personal use only.
// </copyright>


using TermReader.Domain.Entities;

namespace TermReader.Application.Interfaces;

/// <summary>
/// Service for generating audio for multiple articles in parallel with rate limiting
/// </summary>
public interface IParallelAudioGenerator
{
    /// <summary>
    /// Generates audio for multiple articles in parallel
    /// </summary>
    /// <param name="articles">Articles to process</param>
    /// <param name="voiceId">Voice ID to use for generation</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result containing successful and failed audio generations</returns>
    Task<AudioGenerationResult> GenerateAudioForArticlesAsync(
        IEnumerable<Article> articles,
        string voiceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Estimates the total cost for generating audio for multiple articles
    /// </summary>
    /// <param name="articles">Articles to estimate cost for</param>
    /// <returns>Estimated total cost in dollars</returns>
    decimal EstimateTotalCost(IEnumerable<Article> articles);

    /// <summary>
    /// Estimates the total character count for multiple articles
    /// </summary>
    /// <param name="articles">Articles to count characters for</param>
    /// <returns>Total character count</returns>
    int EstimateTotalCharacters(IEnumerable<Article> articles);
}
