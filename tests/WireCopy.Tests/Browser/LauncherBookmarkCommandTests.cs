// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Entities.Bookmarks;
using WireCopy.Domain.Entities.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Browser;
using WireCopy.Infrastructure.Browser.CommandHandlers;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// Tests for bookmark reorder (Shift+J/K) and rename (:rename) commands in the launcher.
/// </summary>
[Trait("Category", "Unit")]
public class LauncherBookmarkCommandTests
{
    private readonly NavigationService _navigationService;
    private readonly CommandContext _ctx;
    private readonly RenderOptions _options;
    private readonly IBookmarkService _bookmarkService;
    private bool _renderCalled;
    private bool _refreshBookmarksCalled;

    public LauncherBookmarkCommandTests()
    {
        var logger = Substitute.For<ILogger<NavigationService>>();
        _navigationService = new NavigationService(logger);

        var page = Page.Create(
            "https://example.com",
            "<html><body>Test</body></html>",
            new PageMetadata { Title = "Test" });
        page.SetReadableContent(ReadableContent.Create(
            "Test", "Test content", new List<string> { "Paragraph 1" }));
        _navigationService.NavigateTo(page);

        _options = new RenderOptions
        {
            TerminalWidth = 80,
            TerminalHeight = 24,
            MaxContentWidth = 76
        };

        _bookmarkService = Substitute.For<IBookmarkService>();

        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(IBookmarkService)).Returns(_bookmarkService);

        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(serviceProvider);

        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        var themeProvider = Substitute.For<IThemeProvider>();
        themeProvider.CurrentTheme.Returns(ThemeName.Phosphor);
        var lineCacheManager = new LineCacheManager(_navigationService, themeProvider);

        _ctx = new CommandContext
        {
            NavigationService = _navigationService,
            Renderer = Substitute.For<IPageRenderer>(),
            InputHandler = Substitute.For<IInputHandler>(),
            ScopeFactory = scopeFactory,
            Logger = Substitute.For<ILogger>(),
            PageCache = Substitute.For<IPageCache>(),
            LineCacheManager = lineCacheManager,
            ThemeProvider = themeProvider,
            PreloadService = Substitute.For<IPreloadService>(),
            LayoutVariantProvider = Substitute.For<ILayoutVariantProvider>(),
            NavigateToAsync = (_, _, _) => Task.CompletedTask,
            ForceRefreshAsync = (_, _, _) => Task.CompletedTask,
            InteractiveRefreshAsync = (_, _, _) => Task.CompletedTask,
            OpenInteractiveBrowserAsync = (_, _, _) => Task.CompletedTask,
            SetOverlayPainter = _ => { },
            RenderCurrentPageAsync = (_, _) =>
            {
                _renderCalled = true;
                return Task.CompletedTask;
            },
            RefreshCollectionsAsync = _ => Task.CompletedTask,
            RefreshBookmarksAsync = _ =>
            {
                _refreshBookmarksCalled = true;
                return Task.CompletedTask;
            },
            GetCurrentRenderOptions = () => _options,
            CreateCollectionService = _ => Substitute.For<ICollectionService>(),
            GetReaderViewportHeight = _ => 20,
            GetHierarchicalViewportHeight = _ => 20,
            AdjustScrollForSelection = (_, _) => { },
            ScrollToSearchMatch = (_, _) => { },
        };
    }

    private static List<Bookmark> CreateBookmarks(int count)
    {
        var bookmarks = new List<Bookmark>();
        for (var i = 0; i < count; i++)
        {
            bookmarks.Add(Bookmark.Create($"Bookmark {i}", $"https://example.com/b{i}", i));
        }

        return bookmarks;
    }

    #region ReorderUp (Shift+K)

    // Virtual slot layout (workspace-ul5z): 0 = bookmark[0], 1 = Reading List,
    // N (≥ 2) = bookmark[N-1]. workspace-ej1i fixed the reorder handlers to map
    // through BookmarkIndexFromVirtual — pre-fix they indexed bookmarks with
    // the raw virtual index and moved the wrong bookmark.

    [Fact]
    public async Task ReorderUp_AtVirtual2_MovesBookmarkOne()
    {
        var bookmarks = CreateBookmarks(3);
        _ctx.Bookmarks = bookmarks;
        _navigationService.EnterLauncher();
        _navigationService.LauncherSelectedIndex = 2; // bookmark[1]

        await LauncherCommandHandler.Handle(_ctx,
            new NavigationCommand { Type = CommandType.ReorderUp },
            _options, CancellationToken.None);

        await _bookmarkService.Received(1).MoveBookmarkUpAsync(bookmarks[1].Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReorderUp_RefreshesBookmarks()
    {
        var bookmarks = CreateBookmarks(3);
        _ctx.Bookmarks = bookmarks;
        _navigationService.EnterLauncher();
        _navigationService.LauncherSelectedIndex = 2; // bookmark[1]
        _refreshBookmarksCalled = false;

        await LauncherCommandHandler.Handle(_ctx,
            new NavigationCommand { Type = CommandType.ReorderUp },
            _options, CancellationToken.None);

        _refreshBookmarksCalled.Should().BeTrue();
    }

    [Fact]
    public async Task ReorderUp_CallsRender()
    {
        var bookmarks = CreateBookmarks(3);
        _ctx.Bookmarks = bookmarks;
        _navigationService.EnterLauncher();
        _navigationService.LauncherSelectedIndex = 2;
        _renderCalled = false;

        await LauncherCommandHandler.Handle(_ctx,
            new NavigationCommand { Type = CommandType.ReorderUp },
            _options, CancellationToken.None);

        _renderCalled.Should().BeTrue();
    }

    [Fact]
    public async Task ReorderUp_OnReadingListSlot_DoesNotCallService_AndExplainsWhy()
    {
        var bookmarks = CreateBookmarks(3);
        _ctx.Bookmarks = bookmarks;
        _navigationService.EnterLauncher();
        _navigationService.LauncherSelectedIndex = 1; // Reading List slot

        await LauncherCommandHandler.Handle(_ctx,
            new NavigationCommand { Type = CommandType.ReorderUp },
            _options, CancellationToken.None);

        await _bookmarkService.DidNotReceive().MoveBookmarkUpAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        _navigationService.CurrentContext.StatusMessage.Should().Contain("Reading List",
            "the protected slot must say why nothing moved (workspace-ej1i)");
    }

    [Fact]
    public async Task ReorderUp_AtTop_SetsAlreadyAtTopMessage_WithoutServiceCall()
    {
        var bookmarks = CreateBookmarks(3);
        _ctx.Bookmarks = bookmarks;
        _navigationService.EnterLauncher();
        _navigationService.LauncherSelectedIndex = 0; // bookmark[0] — already first

        await LauncherCommandHandler.Handle(_ctx,
            new NavigationCommand { Type = CommandType.ReorderUp },
            _options, CancellationToken.None);

        await _bookmarkService.DidNotReceive().MoveBookmarkUpAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        _navigationService.CurrentContext.StatusMessage.Should().Be("Already at top",
            "boundary no-ops must be announced (workspace-ej1i.4)");
    }

    [Fact]
    public async Task ReorderUp_WhenServiceThrows_ShowsCouldntMoveAndStillRenders()
    {
        var bookmarks = CreateBookmarks(3);
        _ctx.Bookmarks = bookmarks;
        _navigationService.EnterLauncher();
        _navigationService.LauncherSelectedIndex = 2; // bookmark[1]
        _renderCalled = false;

        _bookmarkService.MoveBookmarkUpAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("DB error")));

        await LauncherCommandHandler.Handle(_ctx,
            new NavigationCommand { Type = CommandType.ReorderUp },
            _options, CancellationToken.None);

        _renderCalled.Should().BeTrue();
        _navigationService.CurrentContext.StatusMessage.Should().Be("Couldn't move bookmark",
            "reorder persistence failures must not be silent (workspace-ej1i.2)");
    }

    #endregion

    #region ReorderDown (Shift+J)

    [Fact]
    public async Task ReorderDown_AtVirtual2_MovesBookmarkOne()
    {
        var bookmarks = CreateBookmarks(3);
        _ctx.Bookmarks = bookmarks;
        _navigationService.EnterLauncher();
        _navigationService.LauncherSelectedIndex = 2; // bookmark[1]

        await LauncherCommandHandler.Handle(_ctx,
            new NavigationCommand { Type = CommandType.ReorderDown },
            _options, CancellationToken.None);

        await _bookmarkService.Received(1).MoveBookmarkDownAsync(bookmarks[1].Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReorderDown_RefreshesBookmarks()
    {
        var bookmarks = CreateBookmarks(3);
        _ctx.Bookmarks = bookmarks;
        _navigationService.EnterLauncher();
        _navigationService.LauncherSelectedIndex = 0;
        _refreshBookmarksCalled = false;

        await LauncherCommandHandler.Handle(_ctx,
            new NavigationCommand { Type = CommandType.ReorderDown },
            _options, CancellationToken.None);

        _refreshBookmarksCalled.Should().BeTrue();
    }

    [Fact]
    public async Task ReorderDown_CallsRender()
    {
        var bookmarks = CreateBookmarks(3);
        _ctx.Bookmarks = bookmarks;
        _navigationService.EnterLauncher();
        _navigationService.LauncherSelectedIndex = 0;
        _renderCalled = false;

        await LauncherCommandHandler.Handle(_ctx,
            new NavigationCommand { Type = CommandType.ReorderDown },
            _options, CancellationToken.None);

        _renderCalled.Should().BeTrue();
    }

    [Fact]
    public async Task ReorderDown_OnReadingListSlot_DoesNotCallService()
    {
        var bookmarks = CreateBookmarks(3);
        _ctx.Bookmarks = bookmarks;
        _navigationService.EnterLauncher();
        _navigationService.LauncherSelectedIndex = 1; // Reading List slot

        await LauncherCommandHandler.Handle(_ctx,
            new NavigationCommand { Type = CommandType.ReorderDown },
            _options, CancellationToken.None);

        await _bookmarkService.DidNotReceive().MoveBookmarkDownAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReorderDown_AtBottom_SetsAlreadyAtBottomMessage_WithoutServiceCall()
    {
        var bookmarks = CreateBookmarks(3);
        _ctx.Bookmarks = bookmarks;
        _navigationService.EnterLauncher();
        // With 3 bookmarks the last slot is virtual 3 = bookmark[2].
        _navigationService.LauncherSelectedIndex = 3;

        await LauncherCommandHandler.Handle(_ctx,
            new NavigationCommand { Type = CommandType.ReorderDown },
            _options, CancellationToken.None);

        await _bookmarkService.DidNotReceive().MoveBookmarkDownAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        _navigationService.CurrentContext.StatusMessage.Should().Be("Already at bottom",
            "boundary no-ops must be announced (workspace-ej1i.4)");
    }

    [Fact]
    public async Task ReorderDown_LastBookmark_WasUnreachablePreFix_NowAddressable()
    {
        // Regression guard: pre-ej1i the guard `idx < Bookmarks.Count` made the
        // last bookmark (virtual index == Bookmarks.Count) unreorderable at all.
        // Moving the SECOND-to-last bookmark down must now work from its
        // virtual slot.
        var bookmarks = CreateBookmarks(4);
        _ctx.Bookmarks = bookmarks;
        _navigationService.EnterLauncher();
        _navigationService.LauncherSelectedIndex = 3; // bookmark[2] of 4 — movable down

        await LauncherCommandHandler.Handle(_ctx,
            new NavigationCommand { Type = CommandType.ReorderDown },
            _options, CancellationToken.None);

        await _bookmarkService.Received(1).MoveBookmarkDownAsync(bookmarks[2].Id, Arg.Any<CancellationToken>());
    }

    #endregion

    #region Rename (:rename)

    [Fact]
    public async Task Rename_CallsBookmarkServiceRename()
    {
        var bookmarks = CreateBookmarks(3);
        _ctx.Bookmarks = bookmarks;
        _navigationService.EnterLauncher();
        _navigationService.LauncherSelectedIndex = 1;

        var result = await SearchCommandHandler.HandleCommandLineInput(
            _ctx, "rename NewName", _options, CancellationToken.None);

        result.Should().BeTrue();
        await _bookmarkService.Received(1).RenameBookmarkAsync(
            bookmarks[1].Id, "NewName", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Rename_RefreshesBookmarks()
    {
        var bookmarks = CreateBookmarks(3);
        _ctx.Bookmarks = bookmarks;
        _navigationService.EnterLauncher();
        _navigationService.LauncherSelectedIndex = 0;
        _refreshBookmarksCalled = false;

        await SearchCommandHandler.HandleCommandLineInput(
            _ctx, "rename NewName", _options, CancellationToken.None);

        _refreshBookmarksCalled.Should().BeTrue();
    }

    [Fact]
    public async Task Rename_SetsStatusMessage()
    {
        var bookmarks = CreateBookmarks(3);
        _ctx.Bookmarks = bookmarks;
        _navigationService.EnterLauncher();
        _navigationService.LauncherSelectedIndex = 0;

        await SearchCommandHandler.HandleCommandLineInput(
            _ctx, "rename My New Bookmark", _options, CancellationToken.None);

        _navigationService.CurrentContext.StatusMessage.Should().Contain("My New Bookmark");
    }

    [Fact]
    public async Task Rename_OnCollectionsTile_DoesNotCallService()
    {
        var bookmarks = CreateBookmarks(3);
        _ctx.Bookmarks = bookmarks;
        _navigationService.EnterLauncher();
        _navigationService.LauncherSelectedIndex = bookmarks.Count; // Collections tile

        await SearchCommandHandler.HandleCommandLineInput(
            _ctx, "rename NewName", _options, CancellationToken.None);

        await _bookmarkService.DidNotReceive().RenameBookmarkAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Rename_WhenServiceThrows_SetsErrorStatus()
    {
        var bookmarks = CreateBookmarks(3);
        _ctx.Bookmarks = bookmarks;
        _navigationService.EnterLauncher();
        _navigationService.LauncherSelectedIndex = 0;

        _bookmarkService.RenameBookmarkAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("DB error")));

        await SearchCommandHandler.HandleCommandLineInput(
            _ctx, "rename Test", _options, CancellationToken.None);

        _navigationService.CurrentContext.StatusMessage.Should().Contain("Failed to rename");
    }

    #endregion
}
