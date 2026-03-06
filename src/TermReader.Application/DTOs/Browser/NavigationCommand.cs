// Educational and personal use only.

namespace TermReader.Application.DTOs.Browser;

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

    // Collections
    SaveToCollection,
    SaveToSpecific,
    SaveAllToReadingList,
    OpenCollections,
    DeleteItem,
    ReorderUp,
    ReorderDown,

    // Launcher
    AddBookmark,
    OpenLauncher,

    // Width control
    IncreaseWidth,
    DecreaseWidth,
    ResetWidth,

    // Theme
    CycleTheme,

    // Terminal
    TerminalResized
}
