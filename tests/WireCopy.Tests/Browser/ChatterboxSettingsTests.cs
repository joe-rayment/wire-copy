// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Infrastructure.Browser;
using WireCopy.Infrastructure.Browser.CommandHandlers;
using WireCopy.Infrastructure.Configuration;
using WireCopy.Infrastructure.Podcast;
using WireCopy.Infrastructure.Podcast.Chatterbox;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// workspace-2xej.7/.8/.9: narration-engine Settings section — row composition,
/// readiness strings, the engine picker dispatch, the sample/expressiveness
/// editors' pure helpers, and the narration-test core over the fake worker.
/// </summary>
[Trait("Category", "Unit")]
public class ChatterboxSettingsTests
{
    private static readonly string FakeWorkerPath =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "fake_chatterbox_worker.py");

    private static bool Python3AndFfmpegAvailable =>
        PathHas("python3") && (PathHas("ffmpeg") || PathHas("ffmpeg.exe"));

    #region Row composition (workspace-2xej.7)

    [Fact]
    public void BuildSetupRows_OpenAiEngine_ShowsOpenAiTtsRows()
    {
        var rows = SettingsCommandHandler.BuildSetupRows(engineIsChatterbox: false);

        rows.Take(4).Should().Equal(
            SettingsCommandHandler.SetupRow.OpenAiKey,
            SettingsCommandHandler.SetupRow.LayoutModel,
            SettingsCommandHandler.SetupRow.GcsKey,
            SettingsCommandHandler.SetupRow.GcsBucket);
        rows[4].Should().Be(SettingsCommandHandler.SetupRow.NarrationEngine);
        rows.Should().Contain([
            SettingsCommandHandler.SetupRow.Voice,
            SettingsCommandHandler.SetupRow.Model,
            SettingsCommandHandler.SetupRow.TtsInstructions,
        ]);
        rows.Should().NotContain([
            SettingsCommandHandler.SetupRow.ChatterboxSample,
            SettingsCommandHandler.SetupRow.ChatterboxExaggeration,
            SettingsCommandHandler.SetupRow.ChatterboxStatus,
        ]);
        rows.TakeLast(3).Should().Equal(
            SettingsCommandHandler.SetupRow.OutputFolder,
            SettingsCommandHandler.SetupRow.AutoPurgeHours,
            SettingsCommandHandler.SetupRow.CostGateAlwaysShow);
    }

    [Fact]
    public void BuildSetupRows_ChatterboxEngine_ShowsChatterboxRows()
    {
        var rows = SettingsCommandHandler.BuildSetupRows(engineIsChatterbox: true);

        rows[4].Should().Be(SettingsCommandHandler.SetupRow.NarrationEngine);
        rows.Should().Contain([
            SettingsCommandHandler.SetupRow.ChatterboxSample,
            SettingsCommandHandler.SetupRow.ChatterboxExaggeration,
            SettingsCommandHandler.SetupRow.ChatterboxStatus,
        ]);
        rows.Should().NotContain([
            SettingsCommandHandler.SetupRow.Voice,
            SettingsCommandHandler.SetupRow.Model,
            SettingsCommandHandler.SetupRow.TtsInstructions,
        ]);
        rows.Length.Should().Be(SettingsCommandHandler.BuildSetupRows(false).Length,
            "both engines expose the same row count so the cursor clamp is a no-op");
    }

    #endregion

    #region Readiness strings (workspace-2xej.7)

    [Fact]
    public void BuildEngineReadiness_CoversEveryBranch()
    {
        SettingsCommandHandler.BuildEngineReadiness(true, "coral", true, true, null)
            .OpenAi.Should().Be("ready — key configured · voice coral");
        SettingsCommandHandler.BuildEngineReadiness(false, "coral", true, true, null)
            .OpenAi.Should().Be("needs API key — you'll be asked next");
        SettingsCommandHandler.BuildEngineReadiness(true, "coral", false, true, null)
            .Chatterbox.Should().Be("needs uv — not installed");
        SettingsCommandHandler.BuildEngineReadiness(true, "coral", true, false, null)
            .Chatterbox.Should().Be("broken install — worker script missing");
        SettingsCommandHandler.BuildEngineReadiness(true, "coral", true, true, "mine.wav")
            .Chatterbox.Should().Be("ready · sample mine.wav");
        SettingsCommandHandler.BuildEngineReadiness(true, "coral", true, true, null)
            .Chatterbox.Should().Be("ready · built-in voice (add a sample for your own)");
    }

    [Fact]
    public void FormatRelativeTime_Buckets()
    {
        SettingsCommandHandler.FormatRelativeTime(TimeSpan.FromSeconds(20)).Should().Be("just now");
        SettingsCommandHandler.FormatRelativeTime(TimeSpan.FromMinutes(5)).Should().Be("5m ago");
        SettingsCommandHandler.FormatRelativeTime(TimeSpan.FromHours(2)).Should().Be("2h ago");
        SettingsCommandHandler.FormatRelativeTime(TimeSpan.FromDays(3)).Should().Be("3d ago");
        SettingsCommandHandler.FormatRelativeTime(TimeSpan.FromSeconds(-5)).Should().Be("just now");
    }

    #endregion

    #region Engine picker dispatch (workspace-2xej.7)

    [Fact]
    public async Task DispatchRow_NarrationEngine_PickChatterbox_PersistsTtsEngine()
    {
        var harness = new Harness();

        // Picker opens with OpenAI selected (current engine unset → openai);
        // MoveDown selects Chatterbox, Enter picks it.
        harness.InputHandler.WaitForInputAsync(Arg.Any<CancellationToken>())
            .Returns(
                new NavigationCommand { Type = CommandType.MoveDown },
                new NavigationCommand { Type = CommandType.ActivateLink });

        await SettingsCommandHandler.DispatchRowAsync(
            harness.Ctx, harness.Options, SettingsCommandHandler.SetupRow.NarrationEngine, CancellationToken.None);

        harness.SettingsStore.Received(1).Set(
            SettingsCommandHandler.KeyTtsEngine, "chatterbox", Arg.Any<bool>());
    }

    [Fact]
    public async Task DispatchRow_NarrationEngine_Escape_PersistsNothing()
    {
        var harness = new Harness();
        harness.InputHandler.WaitForInputAsync(Arg.Any<CancellationToken>())
            .Returns(new NavigationCommand { Type = CommandType.GoBack });

        await SettingsCommandHandler.DispatchRowAsync(
            harness.Ctx, harness.Options, SettingsCommandHandler.SetupRow.NarrationEngine, CancellationToken.None);

        harness.SettingsStore.DidNotReceive().Set(
            SettingsCommandHandler.KeyTtsEngine, Arg.Any<string>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task DispatchRow_ChatterboxExaggeration_PersistsInvariantValue()
    {
        var harness = new Harness();
        harness.InputHandler
            .PromptForInputAsync(
                Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<bool>(),
                Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<string?>())
            .Returns("0.8");

        await SettingsCommandHandler.DispatchRowAsync(
            harness.Ctx, harness.Options, SettingsCommandHandler.SetupRow.ChatterboxExaggeration, CancellationToken.None);

        harness.SettingsStore.Received(1).Set(
            SettingsCommandHandler.KeyChatterboxExaggeration, "0.8", Arg.Any<bool>());
    }

    [Fact]
    public async Task DispatchRow_ChatterboxSample_ResolvesAndPersistsAbsolutePath()
    {
        var samplePath = Path.Combine(Path.GetTempPath(), $"wirecopy-settings-sample-{Guid.NewGuid():N}.wav");
        await File.WriteAllBytesAsync(samplePath, new byte[] { 1, 2, 3 });
        try
        {
            var harness = new Harness();
            harness.InputHandler
                .PromptForInputAsync(
                    Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<bool>(),
                    Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<string?>())
                .Returns(samplePath);

            await SettingsCommandHandler.DispatchRowAsync(
                harness.Ctx, harness.Options, SettingsCommandHandler.SetupRow.ChatterboxSample, CancellationToken.None);

            harness.SettingsStore.Received(1).Set(
                SettingsCommandHandler.KeyChatterboxVoiceSample, samplePath, Arg.Any<bool>());
        }
        finally
        {
            File.Delete(samplePath);
        }
    }

    #endregion

    #region Sample/expressiveness pure helpers (workspace-2xej.8)

    [Fact]
    public void ResolveSampleInput_AbsolutePathHit()
    {
        var resolved = SettingsCommandHandler.ResolveSampleInput(
            "/data/mine.wav", "/repo", "/home/u", path => path == "/data/mine.wav");

        resolved.Should().Be("/data/mine.wav");
    }

    [Fact]
    public void ResolveSampleInput_BareFilename_FindsVoicesFolder()
    {
        var resolved = SettingsCommandHandler.ResolveSampleInput(
            "mine.wav", "/repo", "/home/u", path => path == Path.GetFullPath("/repo/voices/mine.wav"));

        resolved.Should().Be(Path.GetFullPath("/repo/voices/mine.wav"));
    }

    [Fact]
    public void ResolveSampleInput_CwdWinsOverVoices()
    {
        var resolved = SettingsCommandHandler.ResolveSampleInput(
            "mine.wav", "/repo", "/home/u", path => path == Path.GetFullPath("/repo/mine.wav"));

        resolved.Should().Be(Path.GetFullPath("/repo/mine.wav"));
    }

    [Fact]
    public void ResolveSampleInput_TildeExpandsToHome()
    {
        var resolved = SettingsCommandHandler.ResolveSampleInput(
            "~/clips/mine.wav", "/repo", "/home/u", path => path == Path.GetFullPath("/home/u/clips/mine.wav"));

        resolved.Should().Be(Path.GetFullPath("/home/u/clips/mine.wav"));
    }

    [Fact]
    public void ResolveSampleInput_Miss_ReturnsNull()
    {
        SettingsCommandHandler.ResolveSampleInput("mine.wav", "/repo", "/home/u", _ => false)
            .Should().BeNull();
    }

    [Theory]
    [InlineData("/x/clip.txt", 100, "Unsupported extension")]
    [InlineData("/x/clip.exe", 100, "Unsupported extension")]
    public void ValidateSampleFile_BadExtension_Rejected(string path, long size, string expected)
    {
        SettingsCommandHandler.ValidateSampleFile(path, size).Should().StartWith(expected);
    }

    [Fact]
    public void ValidateSampleFile_TooLarge_Rejected()
    {
        SettingsCommandHandler.ValidateSampleFile("/x/clip.wav", 51L * 1024 * 1024)
            .Should().Contain("not a short clip");
    }

    [Theory]
    [InlineData("/x/clip.wav")]
    [InlineData("/x/clip.mp3")]
    [InlineData("/x/clip.flac")]
    [InlineData("/x/clip.m4a")]
    [InlineData("/x/clip.ogg")]
    public void ValidateSampleFile_SupportedExtensions_Ok(string path)
    {
        SettingsCommandHandler.ValidateSampleFile(path, 1024).Should().BeNull();
    }

    [Theory]
    [InlineData("0.7", true, 0.7f)]
    [InlineData("0,7", true, 0.7f)]
    [InlineData("0", true, 0f)]
    [InlineData("2", true, 2f)]
    [InlineData("abc", false, 0f)]
    [InlineData("-1", false, 0f)]
    [InlineData("3", false, 0f)]
    [InlineData("", false, 0f)]
    public void ParseExaggeration_Cases(string input, bool expectOk, float expectValue)
    {
        var (ok, value, error) = SettingsCommandHandler.ParseExaggeration(input);

        ok.Should().Be(expectOk);
        if (expectOk)
        {
            value.Should().BeApproximately(expectValue, 0.0001f);
            error.Should().BeNull();
        }
        else
        {
            error.Should().Be("Enter a number between 0.0 and 2.0");
        }
    }

    #endregion

    #region Narration test core (workspace-2xej.9)

    [Fact]
    public async Task ExecuteNarrationTest_FakeWorkerPipeline_WritesPlayableClip()
    {
        if (!Python3AndFfmpegAvailable)
        {
            return;
        }

        File.Exists(FakeWorkerPath).Should().BeTrue("fixture must be in test output");
        var config = Options.Create(new ChatterboxConfiguration
        {
            UvPath = "python3",
            UvArgs = string.Empty,
            WorkerRelativePath = FakeWorkerPath,
        });
        await using var sidecar = new ChatterboxSidecar(config, NullLogger<ChatterboxSidecar>.Instance);
        var service = new ChatterboxTtsService(sidecar, config, NullLogger<ChatterboxTtsService>.Instance);
        var outputFolder = Path.Combine(Path.GetTempPath(), $"wirecopy-narration-test-{Guid.NewGuid():N}");

        try
        {
            var outcome = await SettingsCommandHandler.ExecuteNarrationTestAsync(
                service, outputFolder, progress: null, CancellationToken.None);

            outcome.Success.Should().BeTrue(outcome.Error);
            outcome.FilePath.Should().Be(Path.Combine(outputFolder, SettingsCommandHandler.NarrationTestFileName));
            new FileInfo(outcome.FilePath!).Length.Should().BeGreaterThan(0);

            var probed = await FFMpegCore.FFProbe.AnalyseAsync(outcome.FilePath!);
            probed.Duration.TotalSeconds.Should().BeGreaterThan(0, "the clip must contain real audio");
        }
        finally
        {
            if (Directory.Exists(outputFolder))
            {
                Directory.Delete(outputFolder, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ExecuteNarrationTest_EngineFailure_ReturnsErrorOutcome()
    {
        var tts = Substitute.For<ITtsService>();
        tts.GenerateAudioAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IProgress<WireCopy.Application.DTOs.TtsProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(WireCopy.Application.DTOs.TtsGenerationResult.Failure("Local narration: boom"));

        var outcome = await SettingsCommandHandler.ExecuteNarrationTestAsync(
            tts, Path.GetTempPath(), progress: null, CancellationToken.None);

        outcome.Success.Should().BeFalse();
        outcome.Error.Should().Be("Local narration: boom");
        outcome.FilePath.Should().BeNull();
    }

    #endregion

    private static bool PathHas(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator)
            .Any(dir => !string.IsNullOrWhiteSpace(dir) && File.Exists(Path.Combine(dir, name)));

    /// <summary>Minimal CommandContext harness mirroring SettingsCommandHandlerTests.</summary>
    private sealed class Harness
    {
        public Harness()
        {
            InputHandler = Substitute.For<IInputHandler>();
            SettingsStore = Substitute.For<IUserSettingsStore>();

            var openAi = new OpenAiTtsService(
                Microsoft.Extensions.Options.Options.Create(new OpenAiTtsConfiguration { ApiKey = "sk-test" }),
                NullLogger<OpenAiTtsService>.Instance,
                SettingsStore);
            var chatterboxConfig = Microsoft.Extensions.Options.Options.Create(
                new ChatterboxConfiguration { UvPath = "python3", UvArgs = string.Empty });
            var chatterbox = new ChatterboxTtsService(
                new ChatterboxSidecar(chatterboxConfig, NullLogger<ChatterboxSidecar>.Instance),
                chatterboxConfig,
                NullLogger<ChatterboxTtsService>.Instance,
                SettingsStore);

            var scopeFactory = Substitute.For<IServiceScopeFactory>();
            var scope = Substitute.For<IServiceScope>();
            var serviceProvider = Substitute.For<IServiceProvider>();
            serviceProvider.GetService(typeof(IUserSettingsStore)).Returns(SettingsStore);
            serviceProvider.GetService(typeof(OpenAiTtsService)).Returns(openAi);
            serviceProvider.GetService(typeof(ChatterboxTtsService)).Returns(chatterbox);
            serviceProvider.GetService(typeof(ITtsService)).Returns(Substitute.For<ITtsService>());
            scope.ServiceProvider.Returns(serviceProvider);
            scopeFactory.CreateScope().Returns(scope);

            Options = new RenderOptions
            {
                TerminalWidth = 80,
                TerminalHeight = 30,
                MaxContentWidth = 80,
            };

            var navigationService = new NavigationService(Substitute.For<ILogger<NavigationService>>());
            var themeProvider = Substitute.For<IThemeProvider>();
            themeProvider.CurrentTheme.Returns(ThemeName.Phosphor);

            Ctx = new CommandContext
            {
                NavigationService = navigationService,
                Renderer = Substitute.For<IPageRenderer>(),
                InputHandler = InputHandler,
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
                OpenInteractiveBrowserAsync = (_, _, _) => Task.CompletedTask,
                SetOverlayPainter = _ => { },
                RenderCurrentPageAsync = (_, _) => Task.CompletedTask,
                RefreshCollectionsAsync = _ => Task.CompletedTask,
                RefreshBookmarksAsync = _ => Task.CompletedTask,
                GetCurrentRenderOptions = () => Options!,
                CreateCollectionService = _ => Substitute.For<ICollectionService>(),
                GetReaderViewportHeight = _ => 20,
                GetHierarchicalViewportHeight = _ => 20,
                AdjustScrollForSelection = (_, _) => { },
                ScrollToSearchMatch = (_, _) => { },
            };
        }

        public IInputHandler InputHandler { get; }

        public IUserSettingsStore SettingsStore { get; }

        public CommandContext Ctx { get; }

        public RenderOptions Options { get; }
    }
}
