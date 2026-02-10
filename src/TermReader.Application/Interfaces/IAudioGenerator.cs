// <copyright file="IAudioGenerator.cs" company="TermReader">
// Educational and personal use only.
// </copyright>


namespace TermReader.Application.Interfaces;

/// <summary>
/// Service for generating audio from text using Eleven Labs
/// </summary>
public interface IAudioGenerator
{
    /// <summary>
    /// Generates audio from text
    /// </summary>
    /// <param name="text">Text to convert to speech</param>
    /// <param name="voiceId">Eleven Labs voice ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Audio data as byte array</returns>
    Task<byte[]> GenerateAudioAsync(
        string text,
        string voiceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Estimates the cost of generating audio for given text
    /// </summary>
    /// <param name="text">Text to estimate cost for</param>
    /// <returns>Estimated cost in USD</returns>
    decimal EstimateCost(string text);
}
