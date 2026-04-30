// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Domain.Entities.Browser;
using WireCopy.Domain.ValueObjects.Browser;

namespace WireCopy.Application.Interfaces.Browser;

/// <summary>
/// Service for managing browser navigation history.
/// </summary>
public interface INavigationService
{
    /// <summary>
    /// Gets the current navigation context.
    /// </summary>
    NavigationContext CurrentContext { get; }

    /// <summary>
    /// Gets the current page.
    /// </summary>
    Page? CurrentPage { get; }

    /// <summary>
    /// Navigates to a new page, adding it to history.
    /// </summary>
    /// <param name="page">Page to navigate to.</param>
    void NavigateTo(Page page);

    /// <summary>
    /// Navigates back in history.
    /// </summary>
    /// <returns>Previous page or null if at start of history.</returns>
    Page? GoBack();

    /// <summary>
    /// Navigates forward in history.
    /// </summary>
    /// <returns>Next page or null if at end of history.</returns>
    Page? GoForward();

    /// <summary>
    /// Checks if navigation back is possible.
    /// </summary>
    bool CanGoBack { get; }

    /// <summary>
    /// Checks if navigation forward is possible.
    /// </summary>
    bool CanGoForward { get; }

    /// <summary>
    /// Gets the number of pages in back history.
    /// </summary>
    int BackHistoryCount { get; }

    /// <summary>
    /// Gets the number of pages in forward history.
    /// </summary>
    int ForwardHistoryCount { get; }

    /// <summary>
    /// Gets history for display (most recent first).
    /// </summary>
    /// <param name="maxItems">Maximum items to return.</param>
    IEnumerable<string> GetHistoryTitles(int maxItems = 10);

    /// <summary>
    /// Clears all history.
    /// </summary>
    void ClearHistory();

    /// <summary>
    /// Gets whether speed reading mode is currently active.
    /// </summary>
    bool IsSpeedReadActive { get; }

    /// <summary>
    /// Gets the current speed reading rate in words per minute.
    /// </summary>
    int SpeedReadWpm { get; }

    /// <summary>
    /// Starts speed reading mode at the current WPM rate.
    /// </summary>
    void StartSpeedRead();

    /// <summary>
    /// Stops speed reading mode.
    /// </summary>
    void StopSpeedRead();

    /// <summary>
    /// Adjusts the speed reading WPM by the given delta.
    /// </summary>
    void AdjustSpeedReadWpm(int delta);
}
