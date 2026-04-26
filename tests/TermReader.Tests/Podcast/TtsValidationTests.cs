// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using TermReader.Application.DTOs;
using TermReader.Application.Interfaces;
using TermReader.Infrastructure.Configuration;
using TermReader.Infrastructure.Podcast;
using Xunit;

namespace TermReader.Tests.Podcast;

[Trait("Category", "Unit")]
public class TtsValidationTests
{
    #region TtsValidationResult

    [Fact]
    public void Valid_ReturnsIsValidTrue()
    {
        var result = TtsValidationResult.Valid();

        result.IsValid.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
        result.ErrorCode.Should().BeNull();
    }

    [Fact]
    public void Invalid_SetsAllFields()
    {
        var result = TtsValidationResult.Invalid("Key is bad", "invalid_key");

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Be("Key is bad");
        result.ErrorCode.Should().Be("invalid_key");
    }

    #endregion

    #region ValidateApiKeyAsync — No Key

    [Fact]
    public async Task ValidateApiKeyAsync_NoKeyConfigured_ReturnsNoKeyError()
    {
        var service = CreateTtsService(apiKey: null);

        var result = await service.ValidateApiKeyAsync();

        result.IsValid.Should().BeFalse();
        result.ErrorCode.Should().Be("no_key");
        result.ErrorMessage.Should().Contain("No API key");
    }

    [Fact]
    public async Task ValidateApiKeyAsync_EmptyOverride_ReturnsNoKeyError()
    {
        var service = CreateTtsService(apiKey: null);
        service.SetApiKeyOverride(string.Empty);

        var result = await service.ValidateApiKeyAsync();

        result.IsValid.Should().BeFalse();
        result.ErrorCode.Should().Be("no_key");
    }

    #endregion

    #region ValidateApiKeyAsync — Cancellation

    [Fact]
    public async Task ValidateApiKeyAsync_CancelledToken_ThrowsOperationCancelledException()
    {
        var service = CreateTtsService(apiKey: "sk-test");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => service.ValidateApiKeyAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    #endregion

    #region ITtsService Mock — Validation Result Routing

    [Fact]
    public async Task MockValidation_InvalidKey_ReturnsInvalidKeyCode()
    {
        var ttsService = Substitute.For<ITtsService>();
        ttsService.ValidateApiKeyAsync(Arg.Any<CancellationToken>())
            .Returns(TtsValidationResult.Invalid("Invalid API key.", "invalid_key"));

        var result = await ttsService.ValidateApiKeyAsync();

        result.IsValid.Should().BeFalse();
        result.ErrorCode.Should().Be("invalid_key");
        result.ErrorMessage.Should().Be("Invalid API key.");
    }

    [Fact]
    public async Task MockValidation_InsufficientCredits_ReturnsCreditsCode()
    {
        var ttsService = Substitute.For<ITtsService>();
        ttsService.ValidateApiKeyAsync(Arg.Any<CancellationToken>())
            .Returns(TtsValidationResult.Invalid(
                "API key lacks permissions or account has insufficient credits.", "insufficient_credits"));

        var result = await ttsService.ValidateApiKeyAsync();

        result.IsValid.Should().BeFalse();
        result.ErrorCode.Should().Be("insufficient_credits");
    }

    [Fact]
    public async Task MockValidation_RateLimited_ReturnsRateLimitedCode()
    {
        var ttsService = Substitute.For<ITtsService>();
        ttsService.ValidateApiKeyAsync(Arg.Any<CancellationToken>())
            .Returns(TtsValidationResult.Invalid(
                "Rate limited — key is valid but try again shortly.", "rate_limited"));

        var result = await ttsService.ValidateApiKeyAsync();

        result.IsValid.Should().BeFalse();
        result.ErrorCode.Should().Be("rate_limited");
        result.ErrorMessage.Should().Contain("Rate limited");
    }

    [Fact]
    public async Task MockValidation_ServerError_ReturnsServerErrorCode()
    {
        var ttsService = Substitute.For<ITtsService>();
        ttsService.ValidateApiKeyAsync(Arg.Any<CancellationToken>())
            .Returns(TtsValidationResult.Invalid(
                "OpenAI service error — key may be valid, try again.", "server_error"));

        var result = await ttsService.ValidateApiKeyAsync();

        result.IsValid.Should().BeFalse();
        result.ErrorCode.Should().Be("server_error");
    }

    [Fact]
    public async Task MockValidation_Success_ReturnsValid()
    {
        var ttsService = Substitute.For<ITtsService>();
        ttsService.ValidateApiKeyAsync(Arg.Any<CancellationToken>())
            .Returns(TtsValidationResult.Valid());

        var result = await ttsService.ValidateApiKeyAsync();

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task MockValidation_NetworkError_ReturnsNetworkErrorCode()
    {
        var ttsService = Substitute.For<ITtsService>();
        ttsService.ValidateApiKeyAsync(Arg.Any<CancellationToken>())
            .Returns(TtsValidationResult.Invalid("Validation failed: Connection refused", "network_error"));

        var result = await ttsService.ValidateApiKeyAsync();

        result.IsValid.Should().BeFalse();
        result.ErrorCode.Should().Be("network_error");
    }

    #endregion

    #region Hydration Validation Logic

    [Fact]
    public async Task HydrationValidation_InvalidKey_ShouldClearOverride()
    {
        // Simulates the hydration flow: load key, validate, clear if invalid
        var ttsService = Substitute.For<ITtsService>();
        var settingsStore = Substitute.For<IUserSettingsStore>();

        // Simulate saved key
        settingsStore.Get("OpenAiApiKey").Returns("sk-expired-key");
        ttsService.IsConfigured.Returns(false, true, false);

        // Simulate validation failure
        ttsService.ValidateApiKeyAsync(Arg.Any<CancellationToken>())
            .Returns(TtsValidationResult.Invalid("Invalid API key.", "invalid_key"));

        // Execute hydration flow (mirrors PodcastCommandHandler logic)
        var savedKey = settingsStore.Get("OpenAiApiKey");
        if (!string.IsNullOrWhiteSpace(savedKey))
        {
            ttsService.SetApiKeyOverride(savedKey);

            var validation = await ttsService.ValidateApiKeyAsync();
            if (!validation.IsValid && validation.ErrorCode is "invalid_key" or "insufficient_credits")
            {
                settingsStore.Remove("OpenAiApiKey");
                ttsService.SetApiKeyOverride(string.Empty);
            }
        }

        // Verify the key was cleared
        ttsService.Received(1).SetApiKeyOverride("sk-expired-key");
        ttsService.Received(1).SetApiKeyOverride(string.Empty);
        settingsStore.Received(1).Remove("OpenAiApiKey");
    }

    [Fact]
    public async Task HydrationValidation_RateLimited_ShouldKeepKey()
    {
        var ttsService = Substitute.For<ITtsService>();
        var settingsStore = Substitute.For<IUserSettingsStore>();

        settingsStore.Get("OpenAiApiKey").Returns("sk-valid-key");
        ttsService.IsConfigured.Returns(false);
        ttsService.ValidateApiKeyAsync(Arg.Any<CancellationToken>())
            .Returns(TtsValidationResult.Invalid(
                "Rate limited — key is valid but try again shortly.", "rate_limited"));

        // Execute hydration flow
        var savedKey = settingsStore.Get("OpenAiApiKey");
        if (!string.IsNullOrWhiteSpace(savedKey))
        {
            ttsService.SetApiKeyOverride(savedKey);

            var validation = await ttsService.ValidateApiKeyAsync();
            if (!validation.IsValid && validation.ErrorCode is "invalid_key" or "insufficient_credits")
            {
                settingsStore.Remove("OpenAiApiKey");
                ttsService.SetApiKeyOverride(string.Empty);
            }
        }

        // Key should NOT be cleared — rate_limited is not a definitive failure
        ttsService.Received(1).SetApiKeyOverride("sk-valid-key");
        ttsService.DidNotReceive().SetApiKeyOverride(string.Empty);
        settingsStore.DidNotReceive().Remove("OpenAiApiKey");
    }

    [Fact]
    public async Task HydrationValidation_Timeout_ShouldKeepKey()
    {
        var ttsService = Substitute.For<ITtsService>();
        var settingsStore = Substitute.For<IUserSettingsStore>();

        settingsStore.Get("OpenAiApiKey").Returns("sk-valid-key");
        ttsService.IsConfigured.Returns(false);
        ttsService.ValidateApiKeyAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync<OperationCanceledException>();

        // Execute hydration flow (mirrors handler's timeout handling)
        var savedKey = settingsStore.Get("OpenAiApiKey");
        var outerCt = CancellationToken.None;
        if (!string.IsNullOrWhiteSpace(savedKey))
        {
            ttsService.SetApiKeyOverride(savedKey);

            try
            {
                var validation = await ttsService.ValidateApiKeyAsync();
                if (!validation.IsValid && validation.ErrorCode is "invalid_key" or "insufficient_credits")
                {
                    settingsStore.Remove("OpenAiApiKey");
                    ttsService.SetApiKeyOverride(string.Empty);
                }
            }
            catch (OperationCanceledException) when (!outerCt.IsCancellationRequested)
            {
                // Timeout — proceed with saved key
            }
        }

        // Key should NOT be cleared — network timeout allows proceeding
        ttsService.Received(1).SetApiKeyOverride("sk-valid-key");
        ttsService.DidNotReceive().SetApiKeyOverride(string.Empty);
        settingsStore.DidNotReceive().Remove("OpenAiApiKey");
    }

    [Fact]
    public async Task HydrationValidation_ValidKey_ShouldKeepKey()
    {
        var ttsService = Substitute.For<ITtsService>();
        var settingsStore = Substitute.For<IUserSettingsStore>();

        settingsStore.Get("OpenAiApiKey").Returns("sk-valid-key");
        ttsService.IsConfigured.Returns(false);
        ttsService.ValidateApiKeyAsync(Arg.Any<CancellationToken>())
            .Returns(TtsValidationResult.Valid());

        // Execute hydration flow
        var savedKey = settingsStore.Get("OpenAiApiKey");
        if (!string.IsNullOrWhiteSpace(savedKey))
        {
            ttsService.SetApiKeyOverride(savedKey);

            var validation = await ttsService.ValidateApiKeyAsync();
            if (!validation.IsValid && validation.ErrorCode is "invalid_key" or "insufficient_credits")
            {
                settingsStore.Remove("OpenAiApiKey");
                ttsService.SetApiKeyOverride(string.Empty);
            }
        }

        // Key should be kept — validation succeeded
        ttsService.Received(1).SetApiKeyOverride("sk-valid-key");
        ttsService.DidNotReceive().SetApiKeyOverride(string.Empty);
        settingsStore.DidNotReceive().Remove("OpenAiApiKey");
    }

    [Fact]
    public async Task ConfirmationScreen_ValidationSuccess_PersistsKey()
    {
        var ttsService = Substitute.For<ITtsService>();
        var settingsStore = Substitute.For<IUserSettingsStore>();

        ttsService.IsConfigured.Returns(true);
        ttsService.ValidateApiKeyAsync(Arg.Any<CancellationToken>())
            .Returns(TtsValidationResult.Valid());

        // Simulate confirmation screen flow after user enters key
        var trimmedKey = "sk-new-key";
        ttsService.SetApiKeyOverride(trimmedKey);

        var validation = await ttsService.ValidateApiKeyAsync();
        var isTtsConfigured = false;

        if (validation.IsValid)
        {
            isTtsConfigured = true;
            settingsStore.Set("OpenAiApiKey", trimmedKey, encrypt: true);
        }
        else
        {
            ttsService.SetApiKeyOverride(string.Empty);
        }

        isTtsConfigured.Should().BeTrue();
        settingsStore.Received(1).Set("OpenAiApiKey", trimmedKey, encrypt: true);
        ttsService.DidNotReceive().SetApiKeyOverride(string.Empty);
    }

    [Fact]
    public async Task ConfirmationScreen_ValidationFailure_DoesNotPersistAndReverts()
    {
        var ttsService = Substitute.For<ITtsService>();
        var settingsStore = Substitute.For<IUserSettingsStore>();

        ttsService.ValidateApiKeyAsync(Arg.Any<CancellationToken>())
            .Returns(TtsValidationResult.Invalid("Invalid API key.", "invalid_key"));

        // Simulate confirmation screen flow
        var trimmedKey = "sk-bad-key";
        ttsService.SetApiKeyOverride(trimmedKey);

        var validation = await ttsService.ValidateApiKeyAsync();
        var isTtsConfigured = false;

        if (validation.IsValid)
        {
            isTtsConfigured = true;
            settingsStore.Set("OpenAiApiKey", trimmedKey, encrypt: true);
        }
        else
        {
            ttsService.SetApiKeyOverride(string.Empty);
        }

        isTtsConfigured.Should().BeFalse();
        settingsStore.DidNotReceive().Set(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>());
        ttsService.Received(1).SetApiKeyOverride(string.Empty);
    }

    #endregion

    #region Error Code Classification

    [Theory]
    [InlineData("invalid_key", true)]
    [InlineData("insufficient_credits", true)]
    [InlineData("rate_limited", false)]
    [InlineData("server_error", false)]
    [InlineData("network_error", false)]
    [InlineData("no_key", false)]
    public void ErrorCode_IsDefinitiveFailure_MatchesExpectation(string errorCode, bool shouldClear)
    {
        // This mirrors the hydration logic: only clear on definitive failures
        var isDefinitiveFailure = errorCode is "invalid_key" or "insufficient_credits";
        isDefinitiveFailure.Should().Be(shouldClear);
    }

    #endregion

    #region Helpers

    private static OpenAiTtsService CreateTtsService(string? apiKey)
    {
        var config = Options.Create(new OpenAiTtsConfiguration { ApiKey = apiKey });
        return new OpenAiTtsService(config, NullLogger<OpenAiTtsService>.Instance);
    }

    #endregion
}
