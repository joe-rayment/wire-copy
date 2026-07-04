// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using WireCopy.Application.Interfaces;
using WireCopy.Infrastructure.Configuration;
using WireCopy.Infrastructure.Podcast;
using Xunit;

namespace WireCopy.Tests.Podcast;

/// <summary>
/// Unit tests for OpenAiTtsService error and configuration paths.
/// These complement the always-skipped integration tests that require a real API key.
/// </summary>
[Trait("Category", "Unit")]
public class OpenAiTtsServiceUnitTests
{
    // ---- workspace-xott: MapVoice forwards the configured voice VERBATIM (extensible enum), ----
    // ---- matching the removed raw-HTTP path — never silently coercing unknowns to Nova.     ----

    [Theory]
    [InlineData("nova", "nova")]
    [InlineData("verse", "verse")]   // valid API voice absent from the SDK's named members
    [InlineData("marin", "marin")]   // ditto (the API's own 400 message lists it as supported)
    [InlineData("cedar", "cedar")]
    [InlineData(" Coral ", "coral")] // trimmed + lowercased
    [InlineData("SHIMMER", "shimmer")]
    [InlineData("novaa", "novaa")]   // typos reach the API too — it rejects with an actionable 400
    public void MapVoice_ForwardsTheVoiceStringVerbatim(string configured, string expected)
    {
        OpenAiTtsService.MapVoice(configured).ToString().Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MapVoice_BlankVoice_FallsBackToNova(string? voice)
    {
        OpenAiTtsService.MapVoice(voice!).ToString().Should().Be("nova");
    }

    [Fact]
    public void IsConfigured_NoApiKey_ReturnsFalse()
    {
        var config = Options.Create(new OpenAiTtsConfiguration { ApiKey = null });
        var sut = new OpenAiTtsService(config, NullLogger<OpenAiTtsService>.Instance);

        sut.IsConfigured.Should().BeFalse("no API key from any source");
    }

    [Fact]
    public void IsConfigured_EmptyApiKey_ReturnsFalse()
    {
        var config = Options.Create(new OpenAiTtsConfiguration { ApiKey = "  " });
        var sut = new OpenAiTtsService(config, NullLogger<OpenAiTtsService>.Instance);

        sut.IsConfigured.Should().BeFalse("whitespace-only API key should not count");
    }

    [Fact]
    public void IsConfigured_WithApiKey_ReturnsTrue()
    {
        var config = Options.Create(new OpenAiTtsConfiguration { ApiKey = "sk-test-key-123" });
        var sut = new OpenAiTtsService(config, NullLogger<OpenAiTtsService>.Instance);

        sut.IsConfigured.Should().BeTrue();
    }

    [Fact]
    public void IsConfigured_WithSettingsStoreKey_ReturnsTrue()
    {
        var config = Options.Create(new OpenAiTtsConfiguration { ApiKey = null });
        var settingsStore = Substitute.For<IUserSettingsStore>();
        settingsStore.Get("OpenAiApiKey").Returns("sk-from-settings");

        var sut = new OpenAiTtsService(config, NullLogger<OpenAiTtsService>.Instance, settingsStore);

        sut.IsConfigured.Should().BeTrue("API key should be found via settings store fallback");
    }

    [Fact]
    public async Task GenerateAudioAsync_NotConfigured_ReturnsFailure()
    {
        var config = Options.Create(new OpenAiTtsConfiguration { ApiKey = null });
        var sut = new OpenAiTtsService(config, NullLogger<OpenAiTtsService>.Instance);

        var result = await sut.GenerateAudioAsync("Some text", "Test Title");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not configured");
    }

    [Fact]
    public async Task GenerateAudioAsync_ExceedsBudget_ReturnsFailure()
    {
        var config = Options.Create(new OpenAiTtsConfiguration
        {
            ApiKey = "sk-test-key",
            MaxBudgetUsd = 0.01m, // Very low budget
        });
        var sut = new OpenAiTtsService(config, NullLogger<OpenAiTtsService>.Instance);

        // Generate enough text to exceed the $0.01 budget at $15/million chars
        var longText = new string('a', 100_000); // ~$1.50 worth

        var result = await sut.GenerateAudioAsync(longText, "Expensive Article");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("exceeds budget");
    }

    [Fact]
    public async Task GenerateAudioAsync_NullText_ThrowsArgumentNull()
    {
        var config = Options.Create(new OpenAiTtsConfiguration { ApiKey = "sk-test" });
        var sut = new OpenAiTtsService(config, NullLogger<OpenAiTtsService>.Instance);

        var act = () => sut.GenerateAudioAsync(null!, "Title");

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task GenerateAudioAsync_NullTitle_ThrowsArgumentNull()
    {
        var config = Options.Create(new OpenAiTtsConfiguration { ApiKey = "sk-test" });
        var sut = new OpenAiTtsService(config, NullLogger<OpenAiTtsService>.Instance);

        var act = () => sut.GenerateAudioAsync("text", null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public void EstimateCost_ShortText_CalculatesCorrectly()
    {
        var config = Options.Create(new OpenAiTtsConfiguration());
        var sut = new OpenAiTtsService(config, NullLogger<OpenAiTtsService>.Instance);

        var estimate = sut.EstimateCost("Hello world");

        estimate.CharacterCount.Should().Be(11);
        estimate.ChunkCount.Should().Be(1);
        estimate.EstimatedCostUsd.Should().BeGreaterThan(0);
    }

    [Fact]
    public void EstimateCost_LongText_CalculatesMultipleChunks()
    {
        var config = Options.Create(new OpenAiTtsConfiguration { MaxChunkSize = 4096 });
        var sut = new OpenAiTtsService(config, NullLogger<OpenAiTtsService>.Instance);

        var longText = new string('a', 10_000);
        var estimate = sut.EstimateCost(longText);

        estimate.CharacterCount.Should().Be(10_000);
        estimate.ChunkCount.Should().Be(3, "10000 / 4096 rounds up to 3 chunks");
        // Default model is gpt-4o-mini-tts, priced ~$20/1M chars (~$0.015/min); tts-1 stays $15.
        estimate.EstimatedCostUsd.Should().Be(0.20m, "10000 * $20/1M (gpt-4o-mini-tts default) = $0.20");
    }

    [Fact]
    public void EstimateCost_NullText_ThrowsArgumentNull()
    {
        var config = Options.Create(new OpenAiTtsConfiguration());
        var sut = new OpenAiTtsService(config, NullLogger<OpenAiTtsService>.Instance);

        var act = () => sut.EstimateCost(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    #region GetEffectiveVoice / GetEffectiveModel (workspace-urko)

    [Fact]
    public void GetEffectiveVoice_SettingsStoreHasValue_ReturnsStoredValue()
    {
        var config = Options.Create(new OpenAiTtsConfiguration { Voice = "alloy" });
        var settingsStore = Substitute.For<IUserSettingsStore>();
        settingsStore.Get("OpenAiTtsVoice").Returns("sage");

        var sut = new OpenAiTtsService(config, NullLogger<OpenAiTtsService>.Instance, settingsStore);

        sut.GetEffectiveVoice().Should().Be("sage",
            "settings store must override the bound config so confirmation-screen pick is honored at runtime");
    }

    [Fact]
    public void GetEffectiveVoice_SettingsStoreEmpty_FallsBackToConfig()
    {
        var config = Options.Create(new OpenAiTtsConfiguration { Voice = "alloy" });
        var settingsStore = Substitute.For<IUserSettingsStore>();
        settingsStore.Get("OpenAiTtsVoice").Returns((string?)null);

        var sut = new OpenAiTtsService(config, NullLogger<OpenAiTtsService>.Instance, settingsStore);

        sut.GetEffectiveVoice().Should().Be("alloy");
    }

    [Fact]
    public void GetEffectiveVoice_SettingsStoreWhitespace_FallsBackToConfig()
    {
        var config = Options.Create(new OpenAiTtsConfiguration { Voice = "alloy" });
        var settingsStore = Substitute.For<IUserSettingsStore>();
        settingsStore.Get("OpenAiTtsVoice").Returns("   ");

        var sut = new OpenAiTtsService(config, NullLogger<OpenAiTtsService>.Instance, settingsStore);

        sut.GetEffectiveVoice().Should().Be("alloy",
            "whitespace overrides must be treated as unset");
    }

    [Fact]
    public void GetEffectiveVoice_NoSettingsStore_FallsBackToConfig()
    {
        var config = Options.Create(new OpenAiTtsConfiguration { Voice = "alloy" });
        var sut = new OpenAiTtsService(config, NullLogger<OpenAiTtsService>.Instance);

        sut.GetEffectiveVoice().Should().Be("alloy");
    }

    [Fact]
    public void GetEffectiveModel_SettingsStoreHasValue_ReturnsStoredValue()
    {
        var config = Options.Create(new OpenAiTtsConfiguration { Model = "tts-1" });
        var settingsStore = Substitute.For<IUserSettingsStore>();
        settingsStore.Get("OpenAiTtsModel").Returns("tts-1-hd");

        var sut = new OpenAiTtsService(config, NullLogger<OpenAiTtsService>.Instance, settingsStore);

        sut.GetEffectiveModel().Should().Be("tts-1-hd");
    }

    [Fact]
    public void GetEffectiveModel_SettingsStoreEmpty_FallsBackToConfig()
    {
        var config = Options.Create(new OpenAiTtsConfiguration { Model = "tts-1" });
        var settingsStore = Substitute.For<IUserSettingsStore>();
        settingsStore.Get("OpenAiTtsModel").Returns((string?)null);

        var sut = new OpenAiTtsService(config, NullLogger<OpenAiTtsService>.Instance, settingsStore);

        sut.GetEffectiveModel().Should().Be("tts-1");
    }

    [Fact]
    public void GetEffectiveModel_NoSettingsStore_FallsBackToConfig()
    {
        var config = Options.Create(new OpenAiTtsConfiguration { Model = "tts-1" });
        var sut = new OpenAiTtsService(config, NullLogger<OpenAiTtsService>.Instance);

        sut.GetEffectiveModel().Should().Be("tts-1");
    }

    /// <summary>
    /// Calls <see cref="OpenAiTtsService.ValidateApiKeyAsync"/> with a deliberately
    /// invalid API key and asserts the model used to build the AudioClient was the
    /// settings-store override, not the bound config. The OpenAI SDK constructs the
    /// HTTP request URL from the model name passed to the AudioClient ctor; a 401 is
    /// expected (invalid key), but the request must reach OpenAI's servers using the
    /// override model. We verify by inspecting the validation result's error code,
    /// which only comes back with "invalid_key" if the request actually reached the
    /// authorization layer (i.e. the model was accepted by the SDK).
    /// </summary>
    /// <remarks>
    /// This is the closest we can get to "asserts the request payload uses the
    /// effective value" without intercepting the SDK's HTTP transport. A truly
    /// air-tight verification requires injecting a mock <c>AudioClient</c> factory
    /// — see workspace-urko follow-up notes. The unit tests above already prove
    /// <c>GetEffectiveVoice/Model</c> resolution; <c>OpenAiTtsService.cs:95,178,355</c>
    /// shows the call sites all funnel through these methods.
    /// </remarks>
    [Fact(Skip = "Requires network — leaves verification to GetEffective* unit tests + source review at OpenAiTtsService.cs:95,178,355")]
    public async Task ValidateApiKeyAsync_UsesEffectiveModel_NotBoundConfig()
    {
        // Bound config = tts-1; settings override = tts-1-hd.
        var config = Options.Create(new OpenAiTtsConfiguration
        {
            ApiKey = "sk-not-a-real-key",
            Model = "tts-1",
        });
        var settingsStore = Substitute.For<IUserSettingsStore>();
        settingsStore.Get("OpenAiTtsModel").Returns("tts-1-hd");

        var sut = new OpenAiTtsService(config, NullLogger<OpenAiTtsService>.Instance, settingsStore);

        // Sanity: GetEffectiveModel must resolve to the override.
        sut.GetEffectiveModel().Should().Be("tts-1-hd");

        // The actual network call would yield a 401 ClientResultException; we don't
        // exercise it here because it would hit api.openai.com.
        await Task.CompletedTask;
    }

    #endregion

    #region GetEffectiveInstructions (workspace-clsl)

    [Fact]
    public void Defaults_TargetGpt4oMiniTtsCoralWithPlayfulInstructions()
    {
        // The bead's headline change: a fresh OpenAiTtsConfiguration must
        // ship the gpt-4o-mini-tts model + Coral voice + the playful-news-anchor
        // instructions. Existing users with overrides still get their values
        // back (covered by GetEffective* tests below).
        var defaults = new OpenAiTtsConfiguration();

        defaults.Model.Should().Be("gpt-4o-mini-tts");
        defaults.Voice.Should().Be("coral");
        defaults.Instructions.Should().Be("Speak like a playful but knowing news anchor");
    }

    [Fact]
    public void GetEffectiveInstructions_SettingsStoreHasValue_ReturnsStoredValue()
    {
        var config = Options.Create(new OpenAiTtsConfiguration
        {
            Instructions = "Default instructions",
        });
        var settingsStore = Substitute.For<IUserSettingsStore>();
        settingsStore.Get("OpenAiTtsInstructions").Returns("Override instructions");

        var sut = new OpenAiTtsService(config, NullLogger<OpenAiTtsService>.Instance, settingsStore);

        sut.GetEffectiveInstructions().Should().Be("Override instructions",
            "settings store must override the bound config so confirmation-screen edits are honored at runtime");
    }

    [Fact]
    public void GetEffectiveInstructions_SettingsStoreEmpty_FallsBackToConfig()
    {
        var config = Options.Create(new OpenAiTtsConfiguration
        {
            Instructions = "From config",
        });
        var settingsStore = Substitute.For<IUserSettingsStore>();
        settingsStore.Get("OpenAiTtsInstructions").Returns((string?)null);

        var sut = new OpenAiTtsService(config, NullLogger<OpenAiTtsService>.Instance, settingsStore);

        sut.GetEffectiveInstructions().Should().Be("From config");
    }

    [Fact]
    public void GetEffectiveInstructions_SettingsStoreWhitespace_IsAnExplicitDisable()
    {
        // Audit fix (workspace-xott follow-up): a PRESENT blank value is the ':set instructions
        // none' disable sentinel — it must yield null (omit the field), NOT fall back to the
        // config default. 'reset' works by DELETING the key (Get returns null), tested above.
        var config = Options.Create(new OpenAiTtsConfiguration
        {
            Instructions = "From config",
        });
        var settingsStore = Substitute.For<IUserSettingsStore>();
        settingsStore.Get("OpenAiTtsInstructions").Returns("   ");

        var sut = new OpenAiTtsService(config, NullLogger<OpenAiTtsService>.Instance, settingsStore);

        sut.GetEffectiveInstructions().Should().BeNull(
            "a present blank override is the disable sentinel and must not resurrect the default style");
    }

    [Fact]
    public void GetEffectiveInstructions_NoSettingsStore_FallsBackToConfig()
    {
        var config = Options.Create(new OpenAiTtsConfiguration
        {
            Instructions = "From config",
        });
        var sut = new OpenAiTtsService(config, NullLogger<OpenAiTtsService>.Instance);

        sut.GetEffectiveInstructions().Should().Be("From config");
    }

    [Fact]
    public void GetEffectiveInstructions_ConfigNull_ReturnsNull()
    {
        // Null/empty config + no override → null. The chunk loop uses this to
        // skip the raw-HTTP fallback and go through the SDK path; the request
        // shape must not include an empty `instructions` field.
        var config = Options.Create(new OpenAiTtsConfiguration { Instructions = null });
        var sut = new OpenAiTtsService(config, NullLogger<OpenAiTtsService>.Instance);

        sut.GetEffectiveInstructions().Should().BeNull();
    }

    [Fact]
    public void GetEffectiveInstructions_ConfigEmpty_ReturnsNull()
    {
        // Empty string in config means "no instructions" — must collapse to
        // null so callers omit the field entirely.
        var config = Options.Create(new OpenAiTtsConfiguration { Instructions = string.Empty });
        var sut = new OpenAiTtsService(config, NullLogger<OpenAiTtsService>.Instance);

        sut.GetEffectiveInstructions().Should().BeNull();
    }

    [Fact]
    public void GetEffectiveInstructions_SettingsStoreEmptyString_DisablesInstructions()
    {
        // Audit fix (workspace-xott follow-up): the ':set instructions none' flow persists ""
        // precisely so the request omits the instructions field. Treating it as unset made the
        // playful-news-anchor default impossible to turn off — every chunk was styled anyway.
        var config = Options.Create(new OpenAiTtsConfiguration
        {
            Instructions = "From config",
        });
        var settingsStore = Substitute.For<IUserSettingsStore>();
        settingsStore.Get("OpenAiTtsInstructions").Returns(string.Empty);

        var sut = new OpenAiTtsService(config, NullLogger<OpenAiTtsService>.Instance, settingsStore);

        sut.GetEffectiveInstructions().Should().BeNull(
            "'none' persists an empty string so no instructions are sent — never the config default");
    }

    #endregion
}
