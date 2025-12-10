// <copyright file="InworldAudioGenerator.cs" company="NYT Audio Scraper">
// Educational and personal use only.
// </copyright>

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NYTAudioScraper.Application.Interfaces;
using NYTAudioScraper.Infrastructure.Configuration;

namespace NYTAudioScraper.Infrastructure.Audio;

/// <summary>
/// Audio generator using Inworld TTS API as a fallback provider.
/// </summary>
public class InworldAudioGenerator : IAudioGenerator
{
    private readonly HttpClient _httpClient;
    private readonly InworldConfiguration _config;
    private readonly IAudioCache _audioCache;
    private readonly ILogger<InworldAudioGenerator> _logger;

    public InworldAudioGenerator(
        IOptions<InworldConfiguration> config,
        IAudioCache audioCache,
        ILogger<InworldAudioGenerator> logger,
        IHttpClientFactory httpClientFactory)
    {
        _config = config.Value;
        _audioCache = audioCache;
        _logger = logger;
        _httpClient = httpClientFactory.CreateClient("Inworld");

        if (string.IsNullOrEmpty(_config.ApiKey) || string.IsNullOrEmpty(_config.ApiSecret))
        {
            _logger.LogWarning("Inworld API credentials are not fully configured!");
        }
    }

    public async Task<byte[]> GenerateAudioAsync(
        string text,
        string voiceId,
        CancellationToken cancellationToken = default)
    {
        // Check cache first (use Inworld prefix to avoid collisions with ElevenLabs cache)
        var cacheKey = $"inworld:{voiceId}";
        var cachedAudio = await _audioCache.GetAsync(text, cacheKey, cancellationToken);

        if (cachedAudio != null)
        {
            var estimatedCost = EstimateCost(text);
            _logger.LogInformation(
                "🎵 Inworld cache HIT ({Size:N0} bytes) - saved ${Cost:F4}",
                cachedAudio.Length,
                estimatedCost);
            return cachedAudio;
        }

        _logger.LogDebug("Inworld cache MISS");

        try
        {
            // Use configured voice if the passed voiceId is for ElevenLabs
            var inworldVoice = IsElevenLabsVoiceId(voiceId) ? _config.DefaultVoiceId : voiceId;

            _logger.LogInformation(
                "Generating audio via Inworld TTS: length={Length}, voice={VoiceId}, model={Model}",
                text.Length,
                inworldVoice,
                _config.Model);

            var estimatedCost = EstimateCost(text);
            _logger.LogInformation("Estimated Inworld cost: ${Cost:F4}", estimatedCost);

            var requestBody = new
            {
                text,
                voiceId = inworldVoice,
                modelId = _config.Model,
            };

            var jsonContent = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            // Inworld TTS API endpoint
            var url = $"{_config.BaseUrl}/tts/v1/voice:stream";

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Content = content;

            // Basic auth with API key:secret
            var credentials = $"{_config.ApiKey}:{_config.ApiSecret}";
            var authBytes = Encoding.UTF8.GetBytes(credentials);
            var authBase64 = Convert.ToBase64String(authBytes);
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", authBase64);

            var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
            var responseDoc = JsonDocument.Parse(responseJson);

            // Extract base64 audio content from response
            if (!responseDoc.RootElement.TryGetProperty("audioContent", out var audioContentElement))
            {
                throw new InvalidOperationException("Inworld response missing 'audioContent' field");
            }

            var audioBase64 = audioContentElement.GetString()
                ?? throw new InvalidOperationException("Inworld audioContent is null");

            var audioData = Convert.FromBase64String(audioBase64);

            _logger.LogInformation("Successfully generated Inworld audio: {Size} bytes", audioData.Length);

            // Cache the generated audio
            await _audioCache.SetAsync(text, cacheKey, audioData, cancellationToken);
            _logger.LogInformation("🎵 Cached Inworld audio ({Size:N0} bytes)", audioData.Length);

            return audioData;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Inworld HTTP error: {StatusCode}", ex.StatusCode);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Inworld error generating audio for text length={Length}", text.Length);
            throw;
        }
    }

    public decimal EstimateCost(string text)
    {
        var characterCount = text.Length;
        var estimatedCost = characterCount * _config.CostPerCharacter;

        _logger.LogDebug(
            "Inworld estimated cost for {CharacterCount} characters: ${Cost:F4}",
            characterCount,
            estimatedCost);

        return estimatedCost;
    }

    /// <summary>
    /// Checks if the voice ID looks like an ElevenLabs ID (long alphanumeric string).
    /// </summary>
    private static bool IsElevenLabsVoiceId(string voiceId)
    {
        // ElevenLabs voice IDs are typically 20+ character alphanumeric strings
        // Inworld voice IDs are simple names like "Ashley", "Dennis", etc.
        return voiceId.Length > 15 && voiceId.All(c => char.IsLetterOrDigit(c));
    }
}
