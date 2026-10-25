// Licensed under the MIT License. See LICENSE in the repository root.

using System.ClientModel;
using OpenAI;
using OpenAI.Audio;
using WireCopy.Application.DTOs;

namespace WireCopy.Infrastructure.Configuration;

/// <summary>
/// Validates an OpenAI API key with one minimal TTS call (~4 characters, well under a cent).
/// Standalone on purpose (workspace-c3ec4.3): narration moved to audio-engine, so the settings
/// screen no longer has a TTS service to borrow a validator from, but the OpenAI key
/// still powers AI layout and article extraction and must be checked when the user sets it.
/// </summary>
public sealed class OpenAiKeyValidator
{
    private const string ValidationModel = "tts-1";

    /// <summary>
    /// Test seam: lets a test point the SDK <see cref="AudioClient"/> at a loopback fake server
    /// (<see cref="OpenAIClientOptions.Endpoint"/>). Null in production.
    /// </summary>
    internal Action<OpenAIClientOptions>? ConfigureClientOptions { get; init; }

    /// <summary>
    /// Validates <paramref name="apiKey"/> against the OpenAI API and classifies the outcome
    /// (<c>no_key</c>, <c>invalid_key</c>, <c>insufficient_credits</c>, <c>bad_request</c>,
    /// <c>rate_limited</c>, <c>server_error</c>, <c>network_error</c>).
    /// </summary>
    /// <param name="apiKey">The key to validate; blank means "not configured".</param>
    /// <param name="ct">Cancellation token; cancellation is rethrown, never classified.</param>
    /// <returns>The validation result.</returns>
    public async Task<TtsValidationResult> ValidateAsync(string? apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return TtsValidationResult.Invalid("No API key configured.", "no_key");
        }

        try
        {
            var clientOptions = new OpenAIClientOptions();
            ConfigureClientOptions?.Invoke(clientOptions);
            var audioClient = new AudioClient(ValidationModel, new ApiKeyCredential(apiKey.Trim()), clientOptions);
            var options = new SpeechGenerationOptions { ResponseFormat = GeneratedSpeechFormat.Mp3 };

            // Minimal TTS call (~4 chars, cost ~$0.00006) to validate the key end to end.
            var result = await audioClient.GenerateSpeechAsync("test", GeneratedSpeechVoice.Nova, options, ct).ConfigureAwait(false);

            // Discard audio bytes — we only care that the call succeeded.
            _ = result.Value;

            return TtsValidationResult.Valid();
        }
        catch (ClientResultException ex) when (ex.Status == 401)
        {
            return TtsValidationResult.Invalid("Invalid API key.", "invalid_key");
        }
        catch (ClientResultException ex) when (ex.Status == 403)
        {
            return TtsValidationResult.Invalid(
                "API key lacks permissions or account has insufficient credits.", "insufficient_credits");
        }
        catch (ClientResultException ex) when (ex.Status == 400)
        {
            return TtsValidationResult.Invalid(
                $"Key looks valid, but the request was rejected: {ex.Message}", "bad_request");
        }
        catch (ClientResultException ex) when (ex.Status == 429)
        {
            return TtsValidationResult.Invalid(
                "Rate limited — key is valid but try again shortly.", "rate_limited");
        }
        catch (ClientResultException ex) when (ex.Status >= 500)
        {
            return TtsValidationResult.Invalid(
                "OpenAI service error — key may be valid, try again.", "server_error");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return TtsValidationResult.Invalid(
                $"Validation failed: {ex.Message}", "network_error");
        }
    }
}
