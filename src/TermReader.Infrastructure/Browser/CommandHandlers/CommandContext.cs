// Educational and personal use only.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TermReader.Application.DTOs.Browser;
using TermReader.Application.Interfaces;
using TermReader.Application.Interfaces.Browser;
using TermReader.Domain.Entities.Browser;
using TermReader.Domain.Entities.Collections;

namespace TermReader.Infrastructure.Browser.CommandHandlers;

/// <summary>
/// Shared context passed to all command handlers, providing access to
/// orchestrator state and services without coupling to BrowserOrchestrator.
/// </summary>
internal class CommandContext
{
    public required NavigationService NavigationService { get; init; }

    public required IPageRenderer Renderer { get; init; }

    public required IInputHandler InputHandler { get; init; }

    public required IServiceScopeFactory ScopeFactory { get; init; }

    public required ILogger Logger { get; init; }

    public required IPageCache PageCache { get; init; }

    // Mutable shared state
    public List<Collection>? Collections { get; set; }

    public Guid? DefaultCollectionId { get; set; }

    public List<Domain.Entities.Bookmarks.Bookmark>? Bookmarks { get; set; }

    public List<string>? CachedLines { get; set; }

    public int CachedWidth { get; set; }

    // Content width override
    public int? ContentWidthOverride { get; set; }

    // Delegates for operations that remain in BrowserOrchestrator
    public required Func<string, RenderOptions, CancellationToken, Task> NavigateToAsync { get; init; }

    public required Func<string, RenderOptions, CancellationToken, Task> ForceRefreshAsync { get; init; }

    public required Func<RenderOptions, CancellationToken, Task> RenderCurrentPageAsync { get; init; }

    public required Func<CancellationToken, Task> RefreshCollectionsAsync { get; init; }

    public required Func<CancellationToken, Task> RefreshBookmarksAsync { get; init; }

    public required Func<RenderOptions> GetCurrentRenderOptions { get; init; }

    public required Func<IServiceScope, ICollectionService> CreateCollectionService { get; init; }

    // Line cache helpers
    public required Action InvalidateLineCache { get; init; }

    public required Action<RenderOptions> EnsureLineCache { get; init; }

    public required Func<RenderOptions, int> GetReaderViewportHeight { get; init; }

    public required Func<RenderOptions, int> GetHierarchicalViewportHeight { get; init; }

    public required Action<NavigationTree?, RenderOptions> AdjustScrollForSelection { get; init; }

    public required Action<int, RenderOptions> ScrollToSearchMatch { get; init; }

    public required Action<RenderOptions> PreserveScrollPositionAfterRewrap { get; init; }
}
