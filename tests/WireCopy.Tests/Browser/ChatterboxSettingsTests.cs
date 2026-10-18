// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.DTOs.Podcast;
using WireCopy.Application.Interfaces;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Application.Interfaces.Podcast;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Infrastructure.Browser;
using WireCopy.Infrastructure.Browser.CommandHandlers;
using WireCopy.Infrastructure.Podcast.AudioEngine;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// workspace-2xej.7/.8/.9 + c3ec4.3: narration-engine Settings section — row
/// composition, readiness strings from the audio-engine status probe, the engine
/// picker dispatch, the sample/expressiveness editors' pure helpers, the Audio
/// engine row's URL validation and status word, and the narration-test core over
/// a substituted <see cref="IPodcastNarrator"/>.
/// </summary>
[Trait("Category", "Unit")]
public class ChatterboxSettingsTests
{
    #region Row composition (workspace-2xej.7, c3ec4.3)

    // Guards:   With OpenAI narration selected, the Setup screen lists the audio-engine
    //           URL/key rows right before the engine picker, shows the OpenAI voice/model/
    //           instructions rows, hides the Chatterbox rows, and keeps "Test narration".
    // Fails if: BuildSetupRows drops AudioEngine/AudioEngineKey, reorders them after
    //           NarrationEngine, leaks a Chatterbox row, or loses NarrationTest.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~ChatterboxSettingsTests.BuildSetupRows_OpenAiEngine_ShowsOpenAiTtsRows"
    [Fact]
    public void BuildSetupRows_OpenAiEngine_ShowsOpenAiTtsRows()
    {
        var rows = SettingsCommandHandler.BuildSetupRows(engineIsChatterbox: false);

        rows.Take(7).Should().Equal(
            SettingsCommandHandler.SetupRow.OpenAiKey,
            SettingsCommandHandler.SetupRow.LayoutModel,
            SettingsCommandHandler.SetupRow.GcsKey,
            SettingsCommandHandler.SetupRow.GcsBucket,
            SettingsCommandHandler.SetupRow.AudioEngine,
            SettingsCommandHandler.SetupRow.AudioEngineKey,
            SettingsCommandHandler.SetupRow.NarrationEngine);
        rows.Should().ContainInOrder(
            SettingsCommandHandler.SetupRow.Voice,
            SettingsCommandHandler.SetupRow.Model,
            SettingsCommandHandler.SetupRow.TtsInstructions,
            SettingsCommandHandler.SetupRow.NarrationTest);
        rows.Should().NotContain([
            SettingsCommandHandler.SetupRow.ChatterboxSample,
            SettingsCommandHandler.SetupRow.ChatterboxExaggeration,
        ]);
        rows.SkipLast(1).TakeLast(3).Should().Equal(
            SettingsCommandHandler.SetupRow.OutputFolder,
            SettingsCommandHandler.SetupRow.AutoPurgeHours,
            SettingsCommandHandler.SetupRow.CostGateAlwaysShow);
        rows[^1].Should().Be(SettingsCommandHandler.SetupRow.BrowserExtension);
    }

    // Guards:   With Chatterbox selected, the Setup screen keeps the audio-engine rows
    //           ahead of the engine picker, shows the sample/expressiveness rows, hides
    //           the OpenAI voice/model/instructions rows, and still offers "Test narration".
    // Fails if: BuildSetupRows(true) omits AudioEngine/AudioEngineKey, drops NarrationTest
    //           for the Chatterbox engine, or the row count drifts from OpenAI's minus one.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~ChatterboxSettingsTests.BuildSetupRows_ChatterboxEngine_ShowsChatterboxRows"
    [Fact]
    public void BuildSetupRows_ChatterboxEngine_ShowsChatterboxRows()
    {
        var rows = SettingsCommandHandler.BuildSetupRows(engineIsChatterbox: true);

        rows.Skip(4).Take(3).Should().Equal(
            SettingsCommandHandler.SetupRow.AudioEngine,
            SettingsCommandHandler.SetupRow.AudioEngineKey,
            SettingsCommandHandler.SetupRow.NarrationEngine);
        rows.Should().ContainInOrder(
            SettingsCommandHandler.SetupRow.ChatterboxSample,
            SettingsCommandHandler.SetupRow.ChatterboxExaggeration,
            SettingsCommandHandler.SetupRow.NarrationTest);
        rows.Should().NotContain([
            SettingsCommandHandler.SetupRow.Voice,
            SettingsCommandHandler.SetupRow.Model,
            SettingsCommandHandler.SetupRow.TtsInstructions,
        ]);
        // c3ec4.3: Chatterbox has one engine row fewer than OpenAI (no instructions
        // row), so the Setup cursor clamp is live when the engine switches.
        rows.Length.Should().Be(SettingsCommandHandler.BuildSetupRows(false).Length - 1);
    }

    #endregion

    #region Readiness strings (workspace-c3ec4.3)

    // Guards:   The engine picker's readiness lines reflect what audio-engine reported:
    //           an unreachable (or never-probed) engine marks both lines unreachable,
    //           an engine is "ready" only when audio-engine says it is configured, and
    //           the Chatterbox line names the voice sample when one is set.
    // Fails if: BuildEngineReadiness reports "ready" for an engine audio-engine has no
    //           key for, ignores Reachable, or stops mentioning the sample file name.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~ChatterboxSettingsTests.BuildEngineReadiness_CoversEveryBranch"
    [Fact]
    public void BuildEngineReadiness_CoversEveryBranch()
    {
        SettingsCommandHandler.BuildEngineReadiness(null, "nova", null)
            .Should().Be(("audio-engine not reachable", "audio-engine not reachable"));

        SettingsCommandHandler.BuildEngineReadiness(Status(reachable: false), "nova", null)
            .Should().Be(("audio-engine not reachable", "audio-engine not reachable"));

        var openAiUnconfigured = SettingsCommandHandler.BuildEngineReadiness(
            Status(openAiConfigured: false), "nova", null);
        openAiUnconfigured.OpenAi.Should().Be("audio-engine has no OpenAI key — add it in audio-engine's settings");
        openAiUnconfigured.Chatterbox.Should().Be("ready · built-in voice");

        SettingsCommandHandler.BuildEngineReadiness(Status(chatterboxConfigured: false), "nova", null)
            .Chatterbox.Should().Be("audio-engine has no chatterbox engine");

        var bothReady = SettingsCommandHandler.BuildEngineReadiness(Status(), "coral", null);
        bothReady.OpenAi.Should().Be("ready — audio-engine has an OpenAI key · voice coral");
        bothReady.Chatterbox.Should().Be("ready · built-in voice");

        SettingsCommandHandler.BuildEngineReadiness(Status(), "nova", "mine.wav")
            .Chatterbox.Should().Be("ready · sample mine.wav");
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

    #region Audio engine row (workspace-c3ec4.3)

    // Guards:   The Audio engine URL field accepts only absolute http/https URLs, so a
    //           typo like "localhost:8080" is rejected before it is saved and the
    //           narrator is pointed at a URL it can actually open.
    // Fails if: ValidateAudioEngineUrl starts accepting bare hosts, other schemes or
    //           blanks, or rejects a plain http://host:port URL.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~ChatterboxSettingsTests.ValidateAudioEngineUrl_Cases"
    [Theory]
    [InlineData("http://127.0.0.1:8080", true)]
    [InlineData("https://audio.example.com/", true)]
    [InlineData("  http://localhost:8080  ", true)]
    [InlineData("localhost:8080", false)]
    [InlineData("ftp://x", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("not a url", false)]
    public void ValidateAudioEngineUrl_Cases(string? value, bool expectValid)
    {
        var error = SettingsCommandHandler.ValidateAudioEngineUrl(value);

        if (expectValid)
        {
            error.Should().BeNull();
        }
        else
        {
            error.Should().NotBeNullOrWhiteSpace();
        }
    }

    // Guards:   The Audio engine row's status word tells the user what is wrong in a
    //           few words: not running, key rejected, no key, engine not configured —
    //           or "checking…" before the first probe lands and "ready ✓" when it's fine.
    // Fails if: DescribeNarratorStatus maps an ErrorKind to the wrong phrase, reports
    //           ready for a non-ready status, or stops showing "checking…" for null.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~ChatterboxSettingsTests.DescribeNarratorStatus_Cases"
    [Fact]
    public void DescribeNarratorStatus_Cases()
    {
        SettingsCommandHandler.DescribeNarratorStatus(null).Should().Be("checking…");
        SettingsCommandHandler.DescribeNarratorStatus(Status()).Should().Be("ready ✓");
        SettingsCommandHandler.DescribeNarratorStatus(NotReady(NarrationErrorKind.Unreachable))
            .Should().Be("not running");
        SettingsCommandHandler.DescribeNarratorStatus(NotReady(NarrationErrorKind.Unauthorized))
            .Should().Be("key rejected");
        SettingsCommandHandler.DescribeNarratorStatus(NotReady(NarrationErrorKind.NotConfigured))
            .Should().Be("no key");
        SettingsCommandHandler.DescribeNarratorStatus(NotReady(NarrationErrorKind.EngineNotConfigured))
            .Should().Be("engine not configured");
        SettingsCommandHandler.DescribeNarratorStatus(NotReady(NarrationErrorKind.Failed))
            .Should().Be("not ready");
        SettingsCommandHandler.DescribeNarratorStatus(NotReady(NarrationErrorKind.Failed, "disk full"))
            .Should().Be("disk full");
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

    #region Narration test core (workspace-2xej.9, c3ec4.3)

    // Guards:   "Test narration" asks the narrator for the fixed test sentence at the
    //           fixed clip path inside the output folder, and on success reports a clip
    //           that actually exists on disk (the folder is created when missing).
    // Fails if: ExecuteNarrationTestAsync passes a different sentence or path to the
    //           narrator, forgets to create the output folder, or reports a path that
    //           is not where the clip was written.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~ChatterboxSettingsTests.ExecuteNarrationTest_Success_WritesClip"
    [Fact]
    public async Task ExecuteNarrationTest_Success_WritesClip()
    {
        var outputFolder = Path.Combine(Path.GetTempPath(), $"wirecopy-narration-test-{Guid.NewGuid():N}");
        var narrator = Substitute.For<IPodcastNarrator>();
        narrator.NarrateTestAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var outputPath = call.ArgAt<string>(1);
                await File.WriteAllBytesAsync(outputPath, new byte[] { 0x49, 0x44, 0x33, 0x04 });
                return new NarrationResult { Success = true, OutputPath = outputPath, Bytes = 4 };
            });

        try
        {
            var outcome = await SettingsCommandHandler.ExecuteNarrationTestAsync(
                narrator, outputFolder, CancellationToken.None);

            var expectedPath = Path.Combine(outputFolder, SettingsCommandHandler.NarrationTestFileName);
            outcome.Success.Should().BeTrue(outcome.Error);
            outcome.Error.Should().BeNull();
            outcome.FilePath.Should().Be(expectedPath);
            File.Exists(expectedPath).Should().BeTrue("the narrator wrote the clip where the handler asked");
            new FileInfo(expectedPath).Length.Should().BeGreaterThan(0);
            await narrator.Received(1).NarrateTestAsync(
                SettingsCommandHandler.NarrationTestSentence, expectedPath, Arg.Any<CancellationToken>());
        }
        finally
        {
            if (Directory.Exists(outputFolder))
            {
                Directory.Delete(outputFolder, recursive: true);
            }
        }
    }

    // Guards:   When audio-engine fails the narration test, the Setup screen gets the
    //           engine's own error message and no clip path — never a "success" with
    //           a missing file.
    // Fails if: ExecuteNarrationTestAsync swallows NarrationResult.ErrorMessage,
    //           reports Success for a failed result, or hands back a FilePath anyway.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~ChatterboxSettingsTests.ExecuteNarrationTest_EngineFailure_ReturnsErrorOutcome"
    [Fact]
    public async Task ExecuteNarrationTest_EngineFailure_ReturnsErrorOutcome()
    {
        var narrator = Substitute.For<IPodcastNarrator>();
        narrator.NarrateTestAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new NarrationResult
            {
                Success = false,
                ErrorMessage = "audio-engine: boom",
                ErrorKind = NarrationErrorKind.Failed,
            });

        var outcome = await SettingsCommandHandler.ExecuteNarrationTestAsync(
            narrator, Path.GetTempPath(), CancellationToken.None);

        outcome.Success.Should().BeFalse();
        outcome.Error.Should().Be("audio-engine: boom");
        outcome.FilePath.Should().BeNull();
    }

    #endregion

    /// <summary>A probe result as audio-engine reports it; defaults to both engines configured and ready.</summary>
    private static NarratorStatus Status(
        bool reachable = true,
        bool openAiConfigured = true,
        bool chatterboxConfigured = true) =>
        new()
        {
            Url = "http://127.0.0.1:8080",
            Reachable = reachable,
            Authorized = reachable,
            Ready = reachable && openAiConfigured,
            EngineName = "openai",
            EngineConfigured = openAiConfigured,
            Engines =
            [
                new NarratorEngine("openai", openAiConfigured, [], "nova"),
                new NarratorEngine("chatterbox", chatterboxConfigured, [], "builtin"),
            ],
            Message = reachable ? "ok" : "connection refused",
            ErrorKind = reachable ? NarrationErrorKind.None : NarrationErrorKind.Unreachable,
        };

    private static NarratorStatus NotReady(NarrationErrorKind kind, string message = "") =>
        new()
        {
            Url = "http://127.0.0.1:8080",
            Reachable = kind != NarrationErrorKind.Unreachable,
            Ready = false,
            Message = message,
            ErrorKind = kind,
        };

    /// <summary>Minimal CommandContext harness mirroring SettingsCommandHandlerTests.</summary>
    private sealed class Harness
    {
        public Harness()
        {
            InputHandler = Substitute.For<IInputHandler>();
            SettingsStore = Substitute.For<IUserSettingsStore>();

            var narrator = Substitute.For<IPodcastNarrator>();
            narrator.CheckAsync(Arg.Any<CancellationToken>()).Returns(Status());

            var scopeFactory = Substitute.For<IServiceScopeFactory>();
            var scope = Substitute.For<IServiceScope>();
            var serviceProvider = Substitute.For<IServiceProvider>();
            serviceProvider.GetService(typeof(IUserSettingsStore)).Returns(SettingsStore);
            serviceProvider.GetService(typeof(IPodcastNarrator)).Returns(narrator);
            serviceProvider.GetService(typeof(IOptions<AudioEngineConfiguration>))
                .Returns(Microsoft.Extensions.Options.Options.Create(new AudioEngineConfiguration()));
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
