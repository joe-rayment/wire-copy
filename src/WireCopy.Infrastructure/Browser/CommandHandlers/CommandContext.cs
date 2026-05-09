// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Entities.Browser;
using WireCopy.Domain.Entities.Collections;

namespace WireCopy.Infrastructure.Browser.CommandHandlers;

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

    public required LineCacheManager LineCacheManager { get; init; }

    public required IThemeProvider ThemeProvider { get; init; }

    public required IPreloadService PreloadService { get; init; }

    public required ILayoutVariantProvider LayoutVariantProvider { get; init; }

    /// <summary>
    /// When true, animation effects (decrypt-reveal, save flash, etc.) are skipped.
    /// Mirrors <see cref="Configuration.BrowserConfiguration.DisableAnimations"/>.
    /// </summary>
    public bool DisableAnimations { get; init; }

    // Mutable shared state
    public IReadOnlyList<Collection>? Collections { get; set; }

    public Guid? DefaultCollectionId { get; set; }

    public IReadOnlyList<Domain.Entities.Bookmarks.Bookmark>? Bookmarks { get; set; }

    // Content width override
    public int? ContentWidthOverride { get; set; }

    // Undo state for deferred destructive actions
    public UndoState? PendingUndo { get; set; }

    /// <summary>
    /// When true, the podcast CTA renders in "Generating" state with a progress bar.
    /// Set by PodcastCommandHandler during active generation.
    /// </summary>
    public bool IsPodcastGenerating { get; set; }

    /// <summary>
    /// Progress fraction (0.0 to 1.0) for the podcast generation progress bar.
    /// Only meaningful when <see cref="IsPodcastGenerating"/> is true.
    /// </summary>
    public double PodcastGenerationProgress { get; set; }

    // Delegates for operations that remain in BrowserOrchestrator
    public required Func<string, RenderOptions, CancellationToken, Task> NavigateToAsync { get; init; }

    public required Func<string, RenderOptions, CancellationToken, Task> ForceRefreshAsync { get; init; }

    public required Func<string, RenderOptions, CancellationToken, Task> InteractiveRefreshAsync { get; init; }

    public required Func<RenderOptions, CancellationToken, Task> RenderCurrentPageAsync { get; init; }

    public required Func<CancellationToken, Task> RefreshCollectionsAsync { get; init; }

    public required Func<CancellationToken, Task> RefreshBookmarksAsync { get; init; }

    public required Func<RenderOptions> GetCurrentRenderOptions { get; init; }

    public required Func<IServiceScope, ICollectionService> CreateCollectionService { get; init; }

    public required Func<RenderOptions, int> GetReaderViewportHeight { get; init; }

    public required Func<RenderOptions, int> GetHierarchicalViewportHeight { get; init; }

    public required Action<NavigationTree?, RenderOptions> AdjustScrollForSelection { get; init; }

    public required Action<int, RenderOptions> ScrollToSearchMatch { get; init; }
}
