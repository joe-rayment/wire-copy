// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Entities.Collections;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Infrastructure.Browser;
using WireCopy.Infrastructure.Browser.CommandHandlers;
using WireCopy.Infrastructure.Browser.Themes;
using WireCopy.Infrastructure.Configuration;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// Tests for the readiness banner shown at the top of the Generate Podcast
/// confirmation screen (workspace-hcqc). The banner is a single sentence
/// that tells the user why they are on this screen and what will happen if
/// they press Generate — it disambiguates the "looks like a settings page"
/// state from "actually ready to generate".
/// </summary>
[Trait("Category", "Unit")]
[Collection("ConsoleOutput")]
public class PodcastReadinessBannerTests
{
    private static readonly ThemePalette Palette = BuiltInThemes.Get(ThemeName.Phosphor);

    [Fact]
    public void Banner_NoTts_SaysNotReadyAndPointsAtTtsKey()
    {
        var (text, color) = PodcastConfirmationScreens.ResolveReadinessBanner(
            Palette, isTtsConfigured: false, isGcsConfigured: false, bucketError: null);

        text.Should().Contain("Not ready");
        text.Should().Contain("OpenAI TTS API key");
        color.Should().Be(Palette.GetWarningFg().AnsiFg);
    }

    [Fact]
    public void Banner_NoTts_StaysNotReadyEvenIfGcsConfigured()
    {
        var (text, color) = PodcastConfirmationScreens.ResolveReadinessBanner(
            Palette, isTtsConfigured: false, isGcsConfigured: true, bucketError: null);

        text.Should().Contain("Not ready");
        color.Should().Be(Palette.GetWarningFg().AnsiFg);
    }

    [Fact]
    public void Banner_TtsButNoGcs_SaysLocalOnlyMode()
    {
        var (text, color) = PodcastConfirmationScreens.ResolveReadinessBanner(
            Palette, isTtsConfigured: true, isGcsConfigured: false, bucketError: null);

        text.Should().Contain("Ready");
        text.Should().Contain("local-only");
        text.ToLowerInvariant().Should().Contain("no rss");
        color.Should().Be(Palette.GetAccentFg().AnsiFg);
    }

    [Fact]
    public void Banner_TtsAndGcs_SaysReadyWithRssPublish()
    {
        var (text, color) = PodcastConfirmationScreens.ResolveReadinessBanner(
            Palette, isTtsConfigured: true, isGcsConfigured: true, bucketError: null);

        text.Should().Contain("Ready");
        text.ToLowerInvariant().Should().Contain("rss feed");
        color.Should().Be(Palette.PromptFg.AnsiFg);
    }

    [Fact]
    public void Banner_BucketError_ExplainsFailureAndOffersLocalFallback()
    {
        var (text, color) = PodcastConfirmationScreens.ResolveReadinessBanner(
            Palette,
            isTtsConfigured: true,
            isGcsConfigured: true,
            bucketError: "permission denied on bucket");

        text.ToLowerInvariant().Should().Contain("bucket");
        text.ToLowerInvariant().Should().Contain("failed verification");
        color.Should().Be(Palette.GetWarningFg().AnsiFg);
    }

    [Fact]
    public void Banner_Text_IsSingleLine()
    {
        foreach (var (tts, gcs, err) in new[]
        {
            (false, false, (string?)null),
            (true, false, null),
            (true, true, null),
            (true, true, "boom"),
        })
        {
            var (text, _) = PodcastConfirmationScreens.ResolveReadinessBanner(
                Palette, tts, gcs, err);

            text.Should().NotContain("\n", "banner is a one-line subtitle");
            text.Should().NotContain("\r");
        }
    }

    /// <summary>
    /// End-to-end render test: drive the confirmation screen to a single
    /// render cycle (the first paint then GoBack), capture Console output,
    /// and verify the banner string is actually printed. This guards against
    /// "banner helper exists but isn't wired into the render path."
    /// </summary>
    [Fact]
    public async Task Banner_IsRenderedAtTopOfConfirmationScreen()
    {
        var ttsService = Substitute.For<ITtsService>();
        ttsService.IsConfigured.Returns(true);

        var inputHandler = Substitute.For<IInputHandler>();
        // First WaitForInputAsync returns GoBack so the loop exits after the
        // first render — we just want the initial paint captured.
        inputHandler.WaitForInputAsync(Arg.Any<CancellationToken>())
            .Returns(_ => new NavigationCommand { Type = CommandType.GoBack });

        var settingsStore = Substitute.For<IUserSettingsStore>();

        var navLogger = Substitute.For<ILogger<NavigationService>>();
        var navigationService = new NavigationService(navLogger);

        var ttsOptions = Substitute.For<IOptions<OpenAiTtsConfiguration>>();
        ttsOptions.Value.Returns(new OpenAiTtsConfiguration());

        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IServiceScope>();
        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(IUserSettingsStore)).Returns(settingsStore);
        serviceProvider.GetService(typeof(IOptions<OpenAiTtsConfiguration>)).Returns(ttsOptions);
        scope.ServiceProvider.Returns(serviceProvider);
        scopeFactory.CreateScope().Returns(scope);

        var options = new RenderOptions
        {
            TerminalWidth = 120,
            TerminalHeight = 40,
            MaxContentWidth = 120,
        };

        var themeProvider = Substitute.For<IThemeProvider>();
        themeProvider.CurrentTheme.Returns(ThemeName.Phosphor);

        var ctx = new CommandContext
        {
            NavigationService = navigationService,
            Renderer = Substitute.For<IPageRenderer>(),
            InputHandler = inputHandler,
            ScopeFactory = scopeFactory,
            Logger = Substitute.For<ILogger>(),
            PageCache = Substitute.For<IPageCache>(),
            LineCacheManager = new LineCacheManager(navigationService, themeProvider),
            ThemeProvider = themeProvider,
            PreloadService = Substitute.For<IPreloadService>(),
            LayoutVariantProvider = Substitute.For<ILayoutVariantProvider>(),
            NavigateToAsync = (_, _, _) => Task.CompletedTask,
            ForceRefreshAsync = (_, _, _) => Task.CompletedTask,
            InteractiveRefreshAsync = (_, _, _) => Task.CompletedTask,
            RenderCurrentPageAsync = (_, _) => Task.CompletedTask,
            RefreshCollectionsAsync = _ => Task.CompletedTask,
            RefreshBookmarksAsync = _ => Task.CompletedTask,
            GetCurrentRenderOptions = () => options,
            CreateCollectionService = _ => Substitute.For<ICollectionService>(),
            GetReaderViewportHeight = _ => 20,
            GetHierarchicalViewportHeight = _ => 20,
            AdjustScrollForSelection = (_, _) => { },
            ScrollToSearchMatch = (_, _) => { },
        };

        var collection = Collection.Create("ITest");
        collection.AddItem("https://example.com/a", "A");
        var gcsConfig = new GcsConfiguration();

        var originalOut = Console.Out;
        using var sw = new StringWriter();
        try
        {
            Console.SetOut(sw);
            await PodcastConfirmationScreens.ShowConfirmationScreenAsync(
                ctx,
                options,
                collection,
                ttsService,
                gcsConfig,
                settingsStore,
                gcsClient: null,
                cacheAnalysis: null,
                preflightBucketError: null,
                preflightFeedUrl: null,
                preflightFeedStatusNote: null,
                CancellationToken.None);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        var captured = sw.ToString();
        // TTS configured, GCS not configured → local-only banner
        captured.Should().Contain("local-only",
            "the readiness banner must be visible in the rendered output");
        captured.Should().Contain("Generate Podcast",
            "the screen header should also be present");
    }
}
