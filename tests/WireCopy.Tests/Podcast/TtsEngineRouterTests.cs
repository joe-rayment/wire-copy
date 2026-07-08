// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WireCopy.Application.Interfaces;
using WireCopy.Application.Interfaces.Podcast;
using WireCopy.Infrastructure.Bookmarks;
using WireCopy.Infrastructure.Browser;
using WireCopy.Infrastructure.Collections;
using WireCopy.Infrastructure.Configuration;
using WireCopy.Infrastructure.Podcast;
using WireCopy.Infrastructure.Podcast.Chatterbox;
using WireCopy.Persistence;
using Xunit;

namespace WireCopy.Tests.Podcast;

[Trait("Category", "Unit")]
public class TtsEngineRouterTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("openai")]
    [InlineData("gibberish-engine")]
    public void Active_DefaultsToOpenAi(string? engineSetting)
    {
        var (router, store) = CreateRouter();
        if (engineSetting is not null)
        {
            store.Set("TtsEngine", engineSetting);
        }

        router.ActiveEngineName.Should().Be("openai");
        router.EstimateCost("some nonzero text").EstimatedCostUsd.Should().BeGreaterThan(
            0m, "the OpenAI engine prices per character; chatterbox is always $0");
    }

    [Fact]
    public void Active_SwitchesBetweenCallsWithoutRestart()
    {
        var (router, store) = CreateRouter();

        store.Set("TtsEngine", "chatterbox");
        router.EstimateCost("some nonzero text").EstimatedCostUsd.Should().Be(0m);

        store.Set("TtsEngine", "openai");
        router.EstimateCost("some nonzero text").EstimatedCostUsd.Should().BeGreaterThan(0m);

        store.Set("TtsEngine", "Chatterbox"); // case-insensitive
        router.EstimateCost("some nonzero text").EstimatedCostUsd.Should().Be(0m);
        router.ActiveEngineName.Should().Be("chatterbox");
    }

    [Fact]
    public void SetApiKeyOverride_WhileChatterboxActive_ReachesOpenAi()
    {
        var (router, store, openAi) = CreateRouterWithOpenAi();
        store.Set("TtsEngine", "chatterbox");
        openAi.IsConfigured.Should().BeFalse("no key configured yet");

        router.SetApiKeyOverride("sk-test-override");

        openAi.IsConfigured.Should().BeTrue("the override must land on the OpenAI service, not chatterbox");
    }

    [Fact]
    public void CacheComponent_OpenAi_ReflectsVoiceAndInstructionsOverrides()
    {
        var (router, store) = CreateRouter();

        var baseline = router.GetTtsConfigCacheComponent();
        baseline.Should().StartWith("openai|");

        store.Set("OpenAiTtsVoice", "onyx");
        var voiceChanged = router.GetTtsConfigCacheComponent();
        voiceChanged.Should().Contain("|onyx|").And.NotBe(baseline);

        store.Set("OpenAiTtsInstructions", "whisper everything");
        var instructionsChanged = router.GetTtsConfigCacheComponent();
        instructionsChanged.Should().EndWith("whisper everything").And.NotBe(voiceChanged);
    }

    [Fact]
    public void CacheComponent_Chatterbox_BuiltinAndExaggerationAndSampleContent()
    {
        var (router, store) = CreateRouter();
        store.Set("TtsEngine", "chatterbox");

        var builtin = router.GetTtsConfigCacheComponent();
        builtin.Should().StartWith("chatterbox|english|builtin|");

        store.Set("ChatterboxExaggeration", "0.9");
        var exaggerated = router.GetTtsConfigCacheComponent();
        exaggerated.Should().Contain("|0.9|").And.NotBe(builtin);

        var samplePath = Path.Combine(Path.GetTempPath(), $"wirecopy-router-sample-{Guid.NewGuid():N}.wav");
        try
        {
            File.WriteAllBytes(samplePath, [1, 2, 3, 4, 5, 6, 7, 8]);
            store.Set("ChatterboxVoiceSample", samplePath);
            var sampled = router.GetTtsConfigCacheComponent();
            sampled.Should().NotContain("builtin").And.NotBe(exaggerated);

            // Same path, different bytes → different fingerprint (content hash, not path hash).
            File.WriteAllBytes(samplePath, [9, 9, 9, 9, 9, 9, 9, 9, 9, 9]);
            var resampled = router.GetTtsConfigCacheComponent();
            resampled.Should().NotBe(sampled);

            File.Delete(samplePath);
            router.GetTtsConfigCacheComponent().Should().Contain("|missing|", "a configured-but-absent sample must not collide with builtin");
        }
        finally
        {
            if (File.Exists(samplePath))
            {
                File.Delete(samplePath);
            }
        }
    }

    [Fact]
    public void Di_ResolvesTtsServiceAndCacheKeyProvider_AsTheSameRouterInstance()
    {
        // Mirror DependencyInjectionTests' composition — UserSettingsStore needs
        // DataProtection, which AddTerminalBrowser wires up.
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddLogging();
        services.AddTerminalBrowser();
        services.AddPersistence();
        services.AddCollections();
        services.AddBookmarks();
        services.AddPodcast();
        using var provider = services.BuildServiceProvider();

        var tts = provider.GetRequiredService<ITtsService>();
        var keyProvider = provider.GetRequiredService<ITtsCacheKeyProvider>();

        tts.Should().BeOfType<TtsEngineRouter>();
        keyProvider.Should().BeSameAs(tts);
    }

    private static (TtsEngineRouter Router, InMemorySettingsStore Store) CreateRouter()
    {
        var (router, store, _) = CreateRouterWithOpenAi();
        return (router, store);
    }

    private static (TtsEngineRouter Router, InMemorySettingsStore Store, OpenAiTtsService OpenAi) CreateRouterWithOpenAi()
    {
        var store = new InMemorySettingsStore();
        var openAi = new OpenAiTtsService(
            Options.Create(new OpenAiTtsConfiguration()),
            NullLogger<OpenAiTtsService>.Instance,
            store);
        var chatterboxConfig = Options.Create(new ChatterboxConfiguration { UvPath = "python3", UvArgs = string.Empty });
        var chatterbox = new ChatterboxTtsService(
            new ChatterboxSidecar(chatterboxConfig, NullLogger<ChatterboxSidecar>.Instance),
            chatterboxConfig,
            NullLogger<ChatterboxTtsService>.Instance,
            store);
        var router = new TtsEngineRouter(openAi, chatterbox, store, NullLogger<TtsEngineRouter>.Instance);
        return (router, store, openAi);
    }

    private sealed class InMemorySettingsStore : IUserSettingsStore
    {
        private readonly Dictionary<string, string> _values = [];

        public string? Get(string key) => _values.TryGetValue(key, out var value) ? value : null;

        public void Set(string key, string value, bool encrypt = false) => _values[key] = value;

        public void Remove(string key) => _values.Remove(key);
    }
}
