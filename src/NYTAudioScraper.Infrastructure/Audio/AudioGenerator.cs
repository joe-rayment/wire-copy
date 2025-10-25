using Microsoft.Extensions.Logging;
using NYTAudioScraper.Application.Interfaces;

namespace NYTAudioScraper.Infrastructure.Audio;

/// <summary>
/// Stub implementation of IAudioGenerator
/// </summary>
public class AudioGenerator : IAudioGenerator
{
    private readonly ILogger<AudioGenerator> _logger;
    private const decimal CostPerCharacter = 0.0003m;

    public AudioGenerator(ILogger<AudioGenerator> logger)
    {
        _logger = logger;
    }

    public Task<byte[]> GenerateAudioAsync(
        string text,
        string voiceId,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("GenerateAudioAsync called for {CharCount} characters (stub implementation)", text.Length);
        return Task.FromResult(Array.Empty<byte>());
    }

    public decimal EstimateCost(string text)
    {
        return text.Length * CostPerCharacter;
    }
}
