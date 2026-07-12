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

    /// <summary>
    /// Public RSS feed URL of the active podcast generation job, or null
    /// when no job is running or the job is local-only (no GCS bucket).
    /// workspace-y41e: surfaced on the reading-list CTA's Generating state
    /// so the user can subscribe in their podcast app before walking away.
    /// </summary>
    public string? PodcastFeedUrl { get; set; }

    /// <summary>
    /// When true, the prefetch detail overlay (workspace-v75w renderer) is
    /// drawn on top of the active view. Toggled by the backslash keybind
    /// (workspace-c8v3); dismissed by toggling again or by Esc/GoBack while
    /// visible. Defaults to false — the panel is hidden until the user asks
    /// for it.
    /// </summary>
    public bool IsPreloadDetailVisible { get; set; }

    // Delegates for operations that remain in BrowserOrchestrator
    public required Func<string, RenderOptions, CancellationToken, Task> NavigateToAsync { get; init; }

    public required Func<string, RenderOptions, CancellationToken, Task> ForceRefreshAsync { get; init; }

    public required Func<string, RenderOptions, CancellationToken, Task> InteractiveRefreshAsync { get; init; }

    /// <summary>
    /// workspace-kdda: opens the URL in the app's headed Chrome (the session
    /// the preloader shares) so the user can clear an active CAPTCHA / login
    /// gate without replacing the current TUI page. Used by the HITL open key (|) on the
    /// link list when the preload service is reporting an active
    /// <c>BlockedAction</c>.
    /// </summary>
    public required Func<string, RenderOptions, CancellationToken, Task> OpenInteractiveBrowserAsync { get; init; }

    /// <summary>
    /// Field bug 2026-07-12: under the single-window shell, "open in browser" must open
    /// INSIDE the app — reveal the pane, drive the lens, hand keys to the page — never
    /// an external OS browser. Returns the tri-state <see cref="PaneOpenResult"/> so the
    /// caller can distinguish "terminal mode, OS browser is fine" from "shell owned it".
    /// Optional: null (tests, minimal contexts) behaves as terminal mode.
    /// </summary>
    public Func<string, Task<PaneOpenResult>>? OpenInPaneAsync { get; init; }

    /// <summary>
    /// workspace-ujxu: installs (or clears with <c>null</c>) an anchored
    /// overlay painter invoked at the end of every page render. Used by
    /// long-lived command handlers — most notably the Ctrl+L strategy
    /// chooser — that want to keep a modal box visible alongside the page
    /// as the user navigates inside the modal's interaction loop.
    /// </summary>
    public required Action<Action<RenderOptions>?> SetOverlayPainter { get; init; }

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
