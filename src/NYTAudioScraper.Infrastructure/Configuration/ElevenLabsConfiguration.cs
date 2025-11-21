// Educational and personal use only.

namespace NYTAudioScraper.Infrastructure.Configuration;

/// <summary>
/// Configuration for Eleven Labs API.
/// </summary>
public class ElevenLabsConfiguration
{
    public const string SectionName = "ElevenLabs";

    public required string ApiKey { get; init; }

    public string BaseUrl { get; init; } = "https://api.elevenlabs.io/v1";

    public string DefaultVoiceId { get; init; } = "21m00Tcm4TlvDq8ikWAM"; // Default voice (Rachel)

    public string Model { get; init; } = "eleven_multilingual_v2";

    public decimal CostPerCharacter { get; init; } = 0.0003m;
}
