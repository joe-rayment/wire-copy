// Educational and personal use only.

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NYTAudioScraper.Application.Interfaces;
using NYTAudioScraper.Infrastructure.Configuration;

namespace NYTAudioScraper.Infrastructure.Audio;

public class AudioGenerator : IAudioGenerator
{
    private readonly HttpClient _httpClient;
    private readonly ElevenLabsConfiguration _config;
    private readonly IAudioCache _audioCache;
    private readonly ILogger<AudioGenerator> _logger;

    public AudioGenerator(
        IOptions<ElevenLabsConfiguration> config,
        IAudioCache audioCache,
        ILogger<AudioGenerator> logger,
        IHttpClientFactory httpClientFactory)
    {
        _config = config.Value;
        _audioCache = audioCache;
        _logger = logger;
        _httpClient = httpClientFactory.CreateClient("ElevenLabs");

        if (string.IsNullOrEmpty(_config.ApiKey))
        {
            _logger.LogWarning("ElevenLabs API key is not configured!");
        }
    }

    public async Task<byte[]> GenerateAudioAsync(
        string text,
        string voiceId,
        CancellationToken cancellationToken = default)
    {
        // Check cache first
        var cachedAudio = await _audioCache.GetAsync(text, voiceId, cancellationToken);

        if (cachedAudio != null)
        {
            var estimatedCost = EstimateCost(text);
            _logger.LogInformation(
                "🎵 Audio cache HIT ({Size:N0} bytes) - saved ${Cost:F4} and ~30s",
                cachedAudio.Length,
                estimatedCost);
            return cachedAudio;
        }

        _logger.LogDebug("Audio cache MISS");

        try
        {
            _logger.LogInformation("Generating audio for text length={Length} with voice={VoiceId}", text.Length, voiceId);

            var estimatedCost = EstimateCost(text);
            _logger.LogInformation("Estimated cost: ${Cost:F4}", estimatedCost);

            var requestBody = new
            {
                text,
                model_id = _config.Model,
                voice_settings = new
                {
                    stability = 0.5,
                    similarity_boost = 0.75,
                    style = 0.0,
                    use_speaker_boost = true
                }
            };

            var jsonContent = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            // ElevenLabs API v1 endpoint
            var url = $"v1/text-to-speech/{voiceId}";
            var response = await _httpClient.PostAsync(url, content, cancellationToken);
            response.EnsureSuccessStatusCode();

            var audioData = await response.Content.ReadAsByteArrayAsync(cancellationToken);

            _logger.LogInformation("Successfully generated audio: {Size} bytes", audioData.Length);

            // Cache the generated audio
            await _audioCache.SetAsync(text, voiceId, audioData, cancellationToken);
            _logger.LogInformation("🎵 Cached audio ({Size:N0} bytes)", audioData.Length);

            return audioData;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error generating audio: {StatusCode}", ex.StatusCode);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating audio for text length={Length}", text.Length);
            throw;
        }
    }

    public decimal EstimateCost(string text)
    {
        var characterCount = text.Length;
        var estimatedCost = characterCount * _config.CostPerCharacter;

        _logger.LogDebug(
            "Estimated cost for {CharacterCount} characters: ${Cost:F4}",
            characterCount,
            estimatedCost);

        return estimatedCost;
    }
}
