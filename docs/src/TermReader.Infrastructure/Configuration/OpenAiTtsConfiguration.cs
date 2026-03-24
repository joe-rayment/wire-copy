// Educational and personal use only.

namespace TermReader.Infrastructure.Configuration;

/// <summary>
/// Configuration for OpenAI text-to-speech generation.
/// </summary>
public class OpenAiTtsConfiguration
{
    public const string SectionName = "OpenAiTts";

    /// <summary>
    /// Gets the OpenAI API key. Nullable; checked at runtime, not startup,
    /// so the app can run without TTS configured.
    /// </summary>
    public string? ApiKey { get; init; }

    /// <summary>
    /// Gets the TTS model to use. Default: tts-1.
    /// </summary>
    public string Model { get; init; } = "tts-1";

    /// <summary>
    /// Gets the voice for audio generation.
    /// Valid values: alloy, ash, ballad, coral, echo, fable, onyx, nova, sage, shimmer.
    /// </summary>
    public string Voice { get; init; } = "nova";

    /// <summary>
    /// Gets the playback speed multiplier. Range: 0.25 to 4.0.
    /// </summary>
    public float Speed { get; init; } = 1.0f;

    /// <summary>
    /// Gets the output audio format. Default: aac.
    /// </summary>
    public string OutputFormat { get; init; } = "aac";

    /// <summary>
    /// Gets the maximum number of characters per TTS request chunk.
    /// OpenAI TTS has a 4096 character limit per request.
    /// </summary>
    public int MaxChunkSize { get; init; } = 4096;

    /// <summary>
    /// Gets the maximum budget in USD for a single run. Prevents runaway costs.
    /// </summary>
    public decimal MaxBudgetUsd { get; init; } = 1.00m;

    /// <summary>
    /// Gets the maximum number of retry attempts for failed TTS requests.
    /// </summary>
    public int MaxRetries { get; init; } = 3;

    /// <summary>
    /// Gets the base delay in milliseconds between retries (exponential backoff).
    /// </summary>
    public int RetryBaseDelayMs { get; init; } = 1000;

    /// <summary>
    /// Gets the delay in milliseconds between successive TTS chunk requests.
    /// Helps avoid rate limiting.
    /// </summary>
    public int InterChunkDelayMs { get; init; } = 200;
}
