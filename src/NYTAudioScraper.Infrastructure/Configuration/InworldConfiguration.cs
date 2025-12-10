// <copyright file="InworldConfiguration.cs" company="NYT Audio Scraper">
// Educational and personal use only.
// </copyright>

namespace NYTAudioScraper.Infrastructure.Configuration;

/// <summary>
/// Configuration for Inworld TTS API (fallback TTS provider).
/// </summary>
public class InworldConfiguration
{
    public const string SectionName = "Inworld";

    /// <summary>
    /// Gets or sets the Inworld API key.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Inworld API secret.
    /// </summary>
    public string ApiSecret { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the base URL for the Inworld TTS API.
    /// </summary>
    public string BaseUrl { get; set; } = "https://api.inworld.ai";

    /// <summary>
    /// Gets or sets the TTS model to use (inworld-tts-1 or inworld-tts-1-max).
    /// </summary>
    public string Model { get; set; } = "inworld-tts-1-max";

    /// <summary>
    /// Gets or sets the default voice ID.
    /// </summary>
    public string DefaultVoiceId { get; set; } = "Ashley";

    /// <summary>
    /// Gets or sets the cost per character for budget tracking.
    /// Based on pricing: $9/100K chars = $0.00009/char for Starter tier.
    /// </summary>
    public decimal CostPerCharacter { get; set; } = 0.00009m;

    /// <summary>
    /// Gets or sets a value indicating whether gets or sets whether Inworld is enabled as a fallback.
    /// </summary>
    public bool Enabled { get; set; } = false;
}
