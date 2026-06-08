// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Application.DTOs.Browser;

/// <summary>
/// Represents a navigation command triggered by user input.
/// </summary>
public record NavigationCommand
{
    /// <summary>
    /// Type of command.
    /// </summary>
    public required CommandType Type { get; init; }

    /// <summary>
    /// Optional target URL (for Navigate commands).
    /// </summary>
    public string? TargetUrl { get; init; }

    /// <summary>
    /// Optional search query (for Search commands).
    /// </summary>
    public string? SearchQuery { get; init; }

    /// <summary>
    /// Optional parameters for the command.
    /// </summary>
    public Dictionary<string, object>? Parameters { get; init; }

    /// <summary>
    /// The raw key character that produced this command (when available).
    /// Useful for screens that need to distinguish keys sharing a CommandType.
    /// </summary>
    public char? RawKeyChar { get; init; }

    /// <summary>
    /// Numeric prefix for motion commands (e.g., 10j moves down 10).
    /// Zero means no prefix (default single motion).
    /// </summary>
    public int Count { get; init; }
}

/// <summary>
/// Types of navigation commands.
/// </summary>
public enum CommandType
{
    // Movement
    MoveUp,
    MoveDown,
    MoveLeft,
    MoveRight,
    PageDown,
    PageUp,
    ParagraphDown,
    ParagraphUp,
    GoToTop,
    GoToBottom,

    // Node manipulation
    ExpandNode,
    CollapseNode,
    ToggleNode,

    // Navigation
    ActivateLink,
    GoBack,
    GoForward,
    Navigate,

    // View switching
    SwitchView,
    SwitchToHierarchical,
    SwitchToReadable,

    // Application
    NoOp,
    Refresh,
    Quit,
    ShowHelp,

    // Command line
    OpenCommandLine,

    // Search
    Search,
    SearchNext,
    SearchPrevious,

    // Selection
    ToggleSelection,

    // Collections
    SaveToCollection,
    SaveToSpecific,
    SaveAllToReadingList,
    OpenCollections,
    DeleteItem,
    ReorderUp,
    ReorderDown,
    GeneratePodcast,
    ClearCollection,

    // Launcher
    AddBookmark,
    OpenLauncher,

    // Width control
    IncreaseWidth,
    DecreaseWidth,
    ResetWidth,

    // Theme
    CycleTheme,

    // Cache
    ForceRefresh,
    InteractiveRefresh,

    // Browser
    OpenInBrowser,
    GoToUrl,
    ToggleBrowserDock,

    // Layout
    ChooseLayout,

    // Debug/Utility
    DumpHtml,

    // Terminal
    TerminalResized,

    // Undo
    Undo,

    // Speed reading
    ToggleSpeedRead,
    SpeedReadFaster,
    SpeedReadSlower,

    // Animation
    AnimationTick,

    // Launcher: jump to a 1-9 index. Carries Count = digit (1..9).
    JumpToIndex,

    // Reader: ask the AI extractor to re-derive selectors for the current
    // article and replace the saved layout config (workspace-2e1k).
    RegenerateArticleLayout,

    // Prefetch: toggle the prefetch detail overlay on/off (workspace-c8v3).
    // Hidden by default; bound to backslash so it works from any non-modal
    // view. Pressing again or Esc dismisses.
    TogglePreloadDetail,

    // Podcast: restore the in-progress podcast modal when a background job
    // is running and the user has detached it via 'D'. Bound to Shift+P
    // (workspace-vkhr) and also via the ":podcast" command-line accelerator.
    // No-op when no active job exists.
    RestorePodcastModal,
}
