// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Infrastructure.Browser;
using WireCopy.Infrastructure.Browser.CommandHandlers;
using WireCopy.Infrastructure.Browser.Themes;
using Xunit;

namespace WireCopy.Tests.Podcast;

/// <summary>
/// workspace-nahg item 1: the cancel-run confirmation is a visual bordered
/// modal (same 3-row inline box the cost-gate uses) read via single
/// keystrokes — y confirms, n / Esc declines — replacing the bare
/// "Cancel podcast generation? (y/n):" text prompt.
/// </summary>
[Trait("Category", "Unit")]
[Collection(WireCopy.Tests.ConsoleSerialCollection.Name)]
public class CancellationConfirmModalTests
{
    private readonly IInputHandler _inputHandler = Substitute.For<IInputHandler>();
    private readonly CommandContext _ctx;
    private readonly RenderOptions _options;

    public CancellationConfirmModalTests()
    {
        _options = new RenderOptions
        {
            TerminalWidth = 100,
            TerminalHeight = 35,
            MaxContentWidth = 100,
        };

        var themeProvider = Substitute.For<IThemeProvider>();
        themeProvider.CurrentTheme.Returns(ThemeName.Phosphor);

        var navLogger = Substitute.For<ILogger<NavigationService>>();
        var navigationService = new NavigationService(navLogger);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IServiceScope>();
        var sp = Substitute.For<IServiceProvider>();
        scope.ServiceProvider.Returns(sp);
        scopeFactory.CreateScope().Returns(scope);

        _ctx = new CommandContext
        {
            NavigationService = navigationService,
            Renderer = Substitute.For<IPageRenderer>(),
            InputHandler = _inputHandler,
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
            GetCurrentRenderOptions = () => _options,
            CreateCollectionService = _ => Substitute.For<ICollectionService>(),
            GetReaderViewportHeight = _ => 20,
            GetHierarchicalViewportHeight = _ => 20,
            AdjustScrollForSelection = (_, _) => { },
            ScrollToSearchMatch = (_, _) => { },
        };
    }

    private void QueueKeys(params NavigationCommand[] commands)
    {
        var index = 0;
        _inputHandler.WaitForInputAsync(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                var i = Interlocked.Increment(ref index) - 1;
                return i < commands.Length
                    ? Task.FromResult(commands[i])
                    : new TaskCompletionSource<NavigationCommand>().Task;
            });
    }

    [Fact]
    public async Task ConfirmKey_Y_ReturnsTrue()
    {
        QueueKeys(new NavigationCommand { Type = CommandType.NoOp, RawKeyChar = 'y' });

        var result = await ConsoleCaptureAsync(() =>
            PodcastProgressScreens.ShowCancellationConfirmAsync(_ctx, CancellationToken.None));

        result.Result.Should().BeTrue("'y' is the single-keystroke confirm");
    }

    [Fact]
    public async Task DeclineKey_N_ReturnsFalse()
    {
        // 'n' maps to SearchNext in the real input handler — the modal must
        // read the raw keystroke, not the command type.
        QueueKeys(new NavigationCommand { Type = CommandType.SearchNext, RawKeyChar = 'n' });

        var result = await ConsoleCaptureAsync(() =>
            PodcastProgressScreens.ShowCancellationConfirmAsync(_ctx, CancellationToken.None));

        result.Result.Should().BeFalse();
    }

    [Fact]
    public async Task Escape_ReturnsFalse()
    {
        QueueKeys(new NavigationCommand { Type = CommandType.GoBack });

        var result = await ConsoleCaptureAsync(() =>
            PodcastProgressScreens.ShowCancellationConfirmAsync(_ctx, CancellationToken.None));

        result.Result.Should().BeFalse("Esc declines — never silently confirms a cancel");
    }

    [Fact]
    public async Task InvalidKey_FlashesAndKeepsAwaitingDecision()
    {
        // An unhandled key must not resolve the modal — it flashes the border
        // (workspace-nahg item 4) and waits for a real y/n.
        QueueKeys(
            new NavigationCommand { Type = CommandType.NoOp, RawKeyChar = 'z' },
            new NavigationCommand { Type = CommandType.NoOp, RawKeyChar = 'y' });

        var result = await ConsoleCaptureAsync(() =>
            PodcastProgressScreens.ShowCancellationConfirmAsync(_ctx, CancellationToken.None));

        result.Result.Should().BeTrue("the modal survives the invalid key and honours the eventual 'y'");
        await _inputHandler.Received(2).WaitForInputAsync(Arg.Any<CancellationToken>());

        // The flash paints the border in the warning colour before restoring.
        var warningFg = BuiltInThemes.Get(ThemeName.Phosphor).GetWarningFg().AnsiFg;
        result.Output.Should().Contain(warningFg,
            "the invalid keystroke must produce a visible warning-coloured border flash");
    }

    [Fact]
    public async Task RendersBorderedBox_WithSummaryAndKeyHints()
    {
        QueueKeys(new NavigationCommand { Type = CommandType.NoOp, RawKeyChar = 'n' });

        var result = await ConsoleCaptureAsync(() =>
            PodcastProgressScreens.ShowCancellationConfirmAsync(_ctx, CancellationToken.None));

        result.Output.Should().Contain("Cancel podcast generation?",
            "the modal carries the question as its summary line");
        result.Output.Should().Contain("[y]").And.Contain("yes");
        result.Output.Should().Contain("[n]").And.Contain("no");
        result.Output.Should().Contain("╭").And.Contain("╰",
            "the confirm is a bordered box, not a bare text prompt");
    }

    [Fact]
    public async Task NeverUsesTextPrompt()
    {
        QueueKeys(new NavigationCommand { Type = CommandType.NoOp, RawKeyChar = 'y' });

        await ConsoleCaptureAsync(() =>
            PodcastProgressScreens.ShowCancellationConfirmAsync(_ctx, CancellationToken.None));

        await _inputHandler.DidNotReceiveWithAnyArgs().PromptForInputAsync(default!, default);
    }

    [Fact]
    public async Task PaintCancellingNotice_ShowsCancellingFeedbackRow()
    {
        // workspace-nahg item 2: after the user confirms a cancel, the modal
        // paints a "Cancelling…" row so the task unwind isn't a silent freeze.
        var result = await ConsoleCaptureAsync(() =>
        {
            PodcastProgressScreens.PaintCancellingNotice(_ctx, _options);
            return Task.FromResult(true);
        });

        result.Output.Should().Contain("Cancelling in-flight articles",
            "the user must see the app is tearing the run down while the await unwinds");
    }

    private static async Task<(T Result, string Output)> ConsoleCaptureAsync<T>(Func<Task<T>> action)
    {
        var originalOut = Console.Out;
        try
        {
            using var sw = new StringWriter();
            Console.SetOut(sw);
            var result = await action();
            return (result, sw.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }
}
