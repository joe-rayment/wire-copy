// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Infrastructure.Browser.Themes;
using WireCopy.Infrastructure.Browser.UI.Components;
using WireCopy.Infrastructure.Browser.UI.Renderers;

namespace WireCopy.Infrastructure.Browser.CommandHandlers;

/// <summary>
/// Handles launcher-mode commands: grid navigation, bookmark management, search.
/// </summary>
internal static class LauncherCommandHandler
{
    public static async Task<bool> Handle(CommandContext ctx, NavigationCommand command, RenderOptions options, CancellationToken ct)
    {
        var totalItems = (ctx.Bookmarks?.Count ?? 0) + 1; // +1 for Collections tile

        // Setup hotkey: lowercase 'c' opens the unified Setup screen from the
        // launcher (workspace-ayt8 + workspace-9qzh; rebound from 'S' to 'c'
        // in workspace-jby8 to avoid the save-letter overlap and the
        // capital-letter ergonomic hit). Intercepts BEFORE the OpenCollections
        // dispatch — at the launcher the Reading List tile already lives at
        // slot 1 (digit 2) so collection-opening via 'c' is redundant; the
        // letter is therefore safe to repurpose for Setup at this screen.
        // Intercepted before the URL-bar typing fall-through below so the
        // keystroke doesn't get typed into the URL field. The welcome banner
        // one-shot is gated on first-run inside the settings handler call.
        if (command.RawKeyChar == 'c')
        {
            // Welcome banner is for genuine first-run (no credential set yet).
            // Once the user has any credential configured, S still opens
            // Setup but without the orientation banner — they already know
            // what Setup is.
            bool firstRun;
            using (var scope = ctx.ScopeFactory.CreateScope())
            {
                var settingsStore = scope.ServiceProvider.GetRequiredService<IUserSettingsStore>();
                firstRun = SettingsCommandHandler.IsFirstRun(settingsStore);
            }

            await SettingsCommandHandler.HandleConfigScreen(
                ctx, options, ct, showWelcomeBanner: firstRun).ConfigureAwait(false);
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
            return true;
        }

        // When URL bar is selected, intercept printable keys to start typing immediately
        if (ctx.NavigationService.LauncherSelectedIndex == -1)
        {
            // Navigation keys: let them through to the switch below
            // Everything else: activate URL input (with the typed char pre-seeded)
            var isNavKey = command.Type is CommandType.MoveDown or CommandType.MoveUp
                or CommandType.CollapseNode or CommandType.ExpandNode
                or CommandType.Quit or CommandType.GoBack
                or CommandType.ActivateLink or CommandType.ShowHelp
                or CommandType.TerminalResized or CommandType.OpenCommandLine
                or CommandType.Undo
                or CommandType.TogglePreloadDetail;

            if (!isNavKey && command.RawKeyChar.HasValue && command.RawKeyChar.Value >= 32)
            {
                await HandleGoToUrl(ctx, options, ct, command.RawKeyChar.Value).ConfigureAwait(false);
                return true;
            }
        }

        switch (command.Type)
        {
            case CommandType.Quit:
                return false;

            case CommandType.GoBack:
                // workspace-c8v3: Esc dismisses the prefetch detail overlay
                // first when it's up; otherwise fall through to the legacy
                // launcher refresh.
                if (ctx.IsPreloadDetailVisible)
                {
                    ctx.IsPreloadDetailVisible = false;
                    var refreshedOptions = ctx.GetCurrentRenderOptions();
                    await ctx.RenderCurrentPageAsync(refreshedOptions, ct).ConfigureAwait(false);
                    break;
                }

                // Re-render launcher (recovers from error pages shown over launcher mode)
                await ctx.RefreshBookmarksAsync(ct).ConfigureAwait(false);
                await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
                break;

            case CommandType.TogglePreloadDetail:
                ctx.IsPreloadDetailVisible = !ctx.IsPreloadDetailVisible;
                var toggleOptions = ctx.GetCurrentRenderOptions();
                await ctx.RenderCurrentPageAsync(toggleOptions, ct).ConfigureAwait(false);
                break;

            case CommandType.MoveDown:
            {
                var cols = GetLayoutColumns(options);
                if (ctx.NavigationService.LauncherSelectedIndex == -1)
                {
                    // From URL bar → first bookmark
                    ctx.NavigationService.LauncherSelectedIndex = 0;
                }
                else
                {
                    var newIndex = LauncherNavigationState.MoveInGrid(ctx.NavigationService.LauncherSelectedIndex, totalItems, 1, cols);
                    ctx.NavigationService.LauncherSelectedIndex = newIndex;
                }

                AdjustLauncherScroll(ctx, options);
                await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
                break;
            }

            case CommandType.MoveUp:
            {
                var cols = GetLayoutColumns(options);
                var currentIdx = ctx.NavigationService.LauncherSelectedIndex;
                if (currentIdx <= 0 && currentIdx != -1)
                {
                    // From top row → URL bar
                    ctx.NavigationService.LauncherSelectedIndex = -1;
                }
                else if (currentIdx != -1)
                {
                    var newIndex = LauncherNavigationState.MoveInGrid(currentIdx, totalItems, 0, cols);
                    ctx.NavigationService.LauncherSelectedIndex = newIndex;
                }

                AdjustLauncherScroll(ctx, options);
                await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
                break;
            }

            case CommandType.CollapseNode: // h = left
            {
                var cols = GetLayoutColumns(options);
                var newIndex = LauncherNavigationState.MoveInGrid(ctx.NavigationService.LauncherSelectedIndex, totalItems, 2, cols);
                ctx.NavigationService.LauncherSelectedIndex = newIndex;
                await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
                break;
            }

            case CommandType.ExpandNode: // l = right
            {
                var cols = GetLayoutColumns(options);
                var newIndex = LauncherNavigationState.MoveInGrid(ctx.NavigationService.LauncherSelectedIndex, totalItems, 3, cols);
                ctx.NavigationService.LauncherSelectedIndex = newIndex;
                await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
                break;
            }

            case CommandType.ActivateLink:
            {
                var idx = ctx.NavigationService.LauncherSelectedIndex;
                var bookmarkCount = ctx.Bookmarks?.Count ?? 0;
                if (idx == -1)
                {
                    // URL bar selected — activate URL input
                    await HandleGoToUrl(ctx, options, ct).ConfigureAwait(false);
                }
                else if (IsReadingListSlot(idx, bookmarkCount))
                {
                    // Reading List sits at virtual index 1 (workspace-ul5z).
                    await CollectionCommandHandler.HandleOpenCollections(ctx, options, ct).ConfigureAwait(false);
                }
                else if (ctx.Bookmarks != null)
                {
                    var bookmarkIdx = BookmarkIndexFromVirtual(idx);
                    if (bookmarkIdx >= 0 && bookmarkIdx < ctx.Bookmarks.Count)
                    {
                        var bookmark = ctx.Bookmarks[bookmarkIdx];
                        await ctx.NavigateToAsync(bookmark.Url, options, ct).ConfigureAwait(false);
                    }
                }

                break;
            }

            case CommandType.AddBookmark:
                await HandleAddBookmark(ctx, options, ct).ConfigureAwait(false);
                break;

            case CommandType.DeleteItem:
            {
                // Commit any previous pending undo before starting a new delete
                await UndoCommandHandler.ClearOnAction(ctx, ct).ConfigureAwait(false);

                var idx = ctx.NavigationService.LauncherSelectedIndex;
                var bookmarkCount = ctx.Bookmarks?.Count ?? 0;

                // Reading List slot is protected (workspace-ul5z): the user cannot
                // delete it via `d` \u2014 they must clear it via the collection screen.
                // workspace-ej1i.1: say so instead of silently doing nothing.
                if (ctx.Bookmarks != null && idx >= 0 && IsReadingListSlot(idx, bookmarkCount))
                {
                    ctx.NavigationService.SetStatusMessage(
                        "Reading List can't be deleted \u2014 clear it from the collection view");
                }
                else if (ctx.Bookmarks != null && idx >= 0)
                {
                    var bookmarkIdx = BookmarkIndexFromVirtual(idx);
                    if (bookmarkIdx >= 0 && bookmarkIdx < ctx.Bookmarks.Count)
                    {
                        var bookmark = ctx.Bookmarks[bookmarkIdx];

                        // Store undo state before removing from in-memory list.
                        // OriginalIndex is the BOOKMARK index (not the virtual
                        // launcher index) so undo can restore the bookmark to
                        // the correct position regardless of slot ordering.
                        ctx.PendingUndo = new UndoState
                        {
                            Kind = UndoActionKind.BookmarkRemoved,
                            CreatedAtUtc = DateTime.UtcNow,
                            ItemTitle = bookmark.Name,
                            BookmarkId = bookmark.Id,
                            BookmarkUrl = bookmark.Url,
                            BookmarkName = bookmark.Name,
                            OriginalIndex = bookmarkIdx,
                        };

                        // Remove from in-memory list only (not persisted yet)
                        var mutableList = ctx.Bookmarks.ToList();
                        mutableList.RemoveAt(bookmarkIdx);
                        ctx.Bookmarks = mutableList;

                        var newTotal = mutableList.Count + 1; // +1 for Reading List slot
                        if (ctx.NavigationService.LauncherSelectedIndex >= newTotal)
                        {
                            ctx.NavigationService.LauncherSelectedIndex = Math.Max(0, newTotal - 1);
                        }

                        ctx.NavigationService.SetStatusMessage($"Removed \u00b7 z:undo", UndoState.UndoWindow);
                    }
                }

                await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
                break;
            }

            case CommandType.ReorderUp:
                await HandleReorderUp(ctx, options, ct).ConfigureAwait(false);
                break;

            case CommandType.ReorderDown:
                await HandleReorderDown(ctx, options, ct).ConfigureAwait(false);
                break;

            case CommandType.OpenCollections:
                await CollectionCommandHandler.HandleOpenCollections(ctx, options, ct).ConfigureAwait(false);
                break;

            case CommandType.GoToUrl:
            case CommandType.OpenInBrowser:
                // Select the URL bar, then activate it
                ctx.NavigationService.LauncherSelectedIndex = -1;
                await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
                await HandleGoToUrl(ctx, options, ct).ConfigureAwait(false);
                break;

            case CommandType.OpenCommandLine:
            {
                var input = await ctx.InputHandler.PromptForInputAsync(":", ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(input))
                {
                    if (!await SearchCommandHandler.HandleCommandLineInput(ctx, input.Trim(), options, ct).ConfigureAwait(false))
                    {
                        return false;
                    }
                }
                else
                {
                    await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
                }

                break;
            }

            case CommandType.Search:
            {
                var query = await ctx.InputHandler.PromptForInputAsync("/", ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(query) && ctx.Bookmarks != null)
                {
                    var matchIdx = ctx.Bookmarks
                        .Select((b, i) => (b, i))
                        .Where(x => x.b.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                        .Select(x => x.i)
                        .FirstOrDefault(-1);
                    if (matchIdx >= 0)
                    {
                        ctx.NavigationService.LauncherSelectedIndex = matchIdx;
                    }
                    else
                    {
                        ctx.NavigationService.SetStatusMessage($"No bookmarks matching '{query}'");
                    }
                }

                await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
                break;
            }

            case CommandType.ShowHelp:
                await ViewCommandHandler.HandleShowHelp(ctx, options, ct).ConfigureAwait(false);
                break;

            case CommandType.PageDown:
            {
                var layout = ComputeVariantLayout(options);
                var halfRows = Math.Max(1, layout.VisibleRows / 2);
                var step = halfRows * layout.Columns;
                ctx.NavigationService.LauncherSelectedIndex =
                    Math.Min(ctx.NavigationService.LauncherSelectedIndex + step, totalItems - 1);
                AdjustLauncherScroll(ctx, options);
                await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
                break;
            }

            case CommandType.PageUp:
            {
                var layout = ComputeVariantLayout(options);
                var halfRows = Math.Max(1, layout.VisibleRows / 2);
                var step = halfRows * layout.Columns;
                ctx.NavigationService.LauncherSelectedIndex =
                    Math.Max(ctx.NavigationService.LauncherSelectedIndex - step, 0);
                AdjustLauncherScroll(ctx, options);
                await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
                break;
            }

            case CommandType.GoToTop:
                ctx.NavigationService.LauncherSelectedIndex = 0;
                ctx.NavigationService.LauncherScrollOffset = 0;
                await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
                break;

            case CommandType.GoToBottom:
                ctx.NavigationService.LauncherSelectedIndex = Math.Max(0, totalItems - 1);
                AdjustLauncherScroll(ctx, options);
                await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
                break;

            case CommandType.Undo:
                await UndoCommandHandler.HandleUndo(ctx, options, ct).ConfigureAwait(false);
                break;

            case CommandType.JumpToIndex:
            {
                // Digit 1-9 jump. Count carries the digit (1-based). With the
                // Reading List slot reserved at virtual index 1 (workspace-ul5z),
                // the digit-to-virtual mapping is 1:1 with the slot's badge:
                //   digit 1 → virtual 0 (bookmark[0])
                //   digit 2 → virtual 1 (Reading List)
                //   digit 3 → virtual 2 (bookmark[1])
                //   digit N → virtual N-1
                // Out-of-range digits are no-ops so the badge advertisement
                // stays honest.
                var bookmarkCount = ctx.Bookmarks?.Count ?? 0;
                var totalSlots = bookmarkCount + 1; // +1 for Reading List slot
                var target = command.Count - 1;
                if (target >= 0 && target < totalSlots)
                {
                    ctx.NavigationService.LauncherSelectedIndex = target;
                    AdjustLauncherScroll(ctx, options);
                    await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
                }

                break;
            }
        }

        return true;
    }

    /// <summary>
    /// Returns true when the given virtual launcher index points at the
    /// Reading List slot. The slot lives at virtual index 1 whenever the user
    /// has at least one bookmark; with zero bookmarks the empty-state screen
    /// renders instead, so the slot has no virtual index in that case.
    /// (workspace-ul5z)
    /// </summary>
    internal static bool IsReadingListSlot(int virtualIdx, int bookmarkCount)
    {
        return bookmarkCount >= 1 && virtualIdx == 1;
    }

    /// <summary>
    /// Translates a virtual launcher index into a bookmark-list index. With
    /// the Reading List slot at virtual index 1, the mapping is:
    ///   virtual 0 → bookmark 0
    ///   virtual 1 → Reading List (no bookmark)
    ///   virtual N (≥ 2) → bookmark N - 1
    /// Returns -1 for the Reading List slot. Caller must guard. (workspace-ul5z)
    /// </summary>
    internal static int BookmarkIndexFromVirtual(int virtualIdx)
    {
        if (virtualIdx <= 0)
        {
            return virtualIdx; // 0 stays 0; negatives propagate (URL-bar sentinel etc.)
        }

        if (virtualIdx == 1)
        {
            return -1; // Reading List slot — no bookmark mapping
        }

        return virtualIdx - 1;
    }

    /// <summary>
    /// Inverse of <see cref="BookmarkIndexFromVirtual"/>: maps a bookmark-list
    /// index back to its virtual launcher slot (Reading List at virtual 1).
    ///   bookmark 0 → virtual 0
    ///   bookmark N (≥ 1) → virtual N + 1
    /// </summary>
    internal static int VirtualIndexFromBookmark(int bookmarkIdx)
    {
        return bookmarkIdx <= 0 ? bookmarkIdx : bookmarkIdx + 1;
    }

    private static async Task HandleGoToUrl(CommandContext ctx, RenderOptions options, CancellationToken ct, char? initialChar = null)
    {
        // Select the URL bar visually. Force the page scroll back to the top
        // so the URL bar is in the viewport — the user can never type into a
        // URL bar that has scrolled off-screen.
        ctx.NavigationService.LauncherSelectedIndex = -1;
        ctx.NavigationService.LauncherScrollOffset = 0;
        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);

        // Calculate URL bar position for inline input (must match RenderUrlBar).
        // The URL bar's vertical position depends on which header variant is shown
        // (large 6-row wordmark vs. narrow single-line title), so we delegate the
        // row math to LauncherRenderer.ComputeUrlBarInputRow rather than hard-coding it.
        var width = Math.Max(1, options.TerminalWidth - 2);
        var barWidth = Math.Clamp(width * 3 / 4, Math.Min(30, width - 4), 70);
        var pad = Math.Max(0, (width - barWidth) / 2);
        var urlBarRow = LauncherRenderer.ComputeUrlBarInputRow(options.TerminalWidth, options.ShowSetupHint);
        var inputCol = pad + 2;  // Inside the box border

        // Pre-seed with the initial character if provided (user started typing on URL bar)
        var seed = initialChar.HasValue ? initialChar.Value.ToString() : null;

        // Prompt directly inside the URL bar
        var input = await ctx.InputHandler.PromptForInputAsync(string.Empty, ct, row: urlBarRow, col: inputCol, initialInput: seed).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(input))
        {
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
            return;
        }

        // workspace-khpe.3: validate the typed target before navigating so a
        // malformed entry (spaces, control characters, empty host) reports a
        // clear message on the launcher instead of an opaque load failure.
        if (!NavigationUrl.TryNormalize(input, out var url, out var urlError))
        {
            ctx.NavigationService.SetStatusMessage(urlError);
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
            return;
        }

        await ctx.NavigateToAsync(url, options, ct).ConfigureAwait(false);
    }

    private static async Task HandleAddBookmark(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        var palette = BuiltInThemes.Get(ctx.ThemeProvider.CurrentTheme);

        var steps = new List<WizardStep>
        {
            new()
            {
                Title = "Add Bookmark",
                Fields =
                [
                    new FormFieldConfig
                    {
                        Label = "Name",
                        Placeholder = "My Favorite Site",
                        Validate = v => string.IsNullOrWhiteSpace(v) ? "Name cannot be empty" : null,
                    },
                    new FormFieldConfig
                    {
                        Label = "URL",
                        Placeholder = "https://example.com",
                        HelpText = "https:// is added automatically if omitted",
                        Validate = v => string.IsNullOrWhiteSpace(v) ? "URL cannot be empty" : null,
                    },
                ],
            },
        };

        var result = await WizardRunner.RunAsync(ctx.InputHandler, steps, palette, ct).ConfigureAwait(false);
        if (result == null)
        {
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
            return;
        }

        var name = result["Name"];
        var url = result["URL"];

        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = "https://" + url;
        }

        try
        {
            using var scope = ctx.ScopeFactory.CreateScope();
            var bookmarkService = scope.ServiceProvider.GetRequiredService<IBookmarkService>();
            await bookmarkService.AddBookmarkAsync(name, url, ct).ConfigureAwait(false);
            await ctx.RefreshBookmarksAsync(ct).ConfigureAwait(false);
            ctx.Logger.LogInformation("Added bookmark: {Name} ({Url})", name, url);
        }
        catch (Exception ex)
        {
            // workspace-xx61: never fail silently — the delete path sets a status,
            // so a failed save must too, or the user assumes the bookmark stuck.
            ctx.Logger.LogWarning(ex, "Failed to add bookmark");
            ctx.NavigationService.SetStatusMessage("Couldn't save bookmark", StatusSeverity.Error);
        }

        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
    }

    private static Task HandleReorderUp(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        return HandleReorder(ctx, options, ct, moveUp: true);
    }

    private static Task HandleReorderDown(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        return HandleReorder(ctx, options, ct, moveUp: false);
    }

    /// <summary>
    /// Shift+K / Shift+J bookmark reordering. The selection is a VIRTUAL
    /// launcher index (Reading List occupies virtual slot 1, workspace-ul5z),
    /// so it is mapped through <see cref="BookmarkIndexFromVirtual"/> before
    /// touching the bookmark list — the pre-ej1i code indexed bookmarks with
    /// the raw virtual index and moved the wrong bookmark for any slot past
    /// the Reading List. Feedback (workspace-ej1i.2/.4): boundary no-ops say
    /// "Already at top/bottom", the protected slot says why it won't move,
    /// and persistence failures report "Couldn't move bookmark".
    /// </summary>
    private static async Task HandleReorder(CommandContext ctx, RenderOptions options, CancellationToken ct, bool moveUp)
    {
        var idx = ctx.NavigationService.LauncherSelectedIndex;
        var bookmarkCount = ctx.Bookmarks?.Count ?? 0;

        if (ctx.Bookmarks != null && idx >= 0)
        {
            if (IsReadingListSlot(idx, bookmarkCount))
            {
                ctx.NavigationService.SetStatusMessage("Reading List stays at slot 2 — it can't be reordered");
            }
            else
            {
                var bookmarkIdx = BookmarkIndexFromVirtual(idx);
                if (bookmarkIdx >= 0 && bookmarkIdx < ctx.Bookmarks.Count)
                {
                    if (moveUp && bookmarkIdx == 0)
                    {
                        ctx.NavigationService.SetStatusMessage("Already at top");
                    }
                    else if (!moveUp && bookmarkIdx == ctx.Bookmarks.Count - 1)
                    {
                        ctx.NavigationService.SetStatusMessage("Already at bottom");
                    }
                    else
                    {
                        var bookmark = ctx.Bookmarks[bookmarkIdx];
                        try
                        {
                            using var scope = ctx.ScopeFactory.CreateScope();
                            var bookmarkService = scope.ServiceProvider.GetRequiredService<IBookmarkService>();
                            if (moveUp)
                            {
                                await bookmarkService.MoveBookmarkUpAsync(bookmark.Id, ct).ConfigureAwait(false);
                            }
                            else
                            {
                                await bookmarkService.MoveBookmarkDownAsync(bookmark.Id, ct).ConfigureAwait(false);
                            }

                            await ctx.RefreshBookmarksAsync(ct).ConfigureAwait(false);

                            // Follow the bookmark to its new position (in virtual space).
                            if (ctx.Bookmarks != null)
                            {
                                var newBookmarkIdx = IndexOfBookmarkById(ctx.Bookmarks, bookmark.Id);
                                if (newBookmarkIdx < 0)
                                {
                                    newBookmarkIdx = moveUp
                                        ? Math.Max(0, bookmarkIdx - 1)
                                        : Math.Min(Math.Max(0, ctx.Bookmarks.Count - 1), bookmarkIdx + 1);
                                }

                                ctx.NavigationService.LauncherSelectedIndex = VirtualIndexFromBookmark(newBookmarkIdx);
                            }
                        }
                        catch (Exception ex)
                        {
                            ctx.Logger.LogWarning(ex, "Failed to move bookmark {Direction}", moveUp ? "up" : "down");
                            ctx.NavigationService.SetStatusMessage("Couldn't move bookmark", StatusSeverity.Error);
                        }
                    }
                }
            }
        }

        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
    }

    private static int IndexOfBookmarkById(IReadOnlyList<Domain.Entities.Bookmarks.Bookmark> bookmarks, Guid id)
    {
        for (var i = 0; i < bookmarks.Count; i++)
        {
            if (bookmarks[i].Id == id)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Adjusts <see cref="NavigationService.LauncherScrollOffset"/> so that the
    /// currently selected bookmark cell is fully within the launcher viewport.
    /// </summary>
    /// <remarks>
    /// The launcher is rendered as a single virtual content stream:
    /// <c>[wordmark | URL bar | bookmark grid]</c>. The scroll offset is the
    /// number of *page lines* that have scrolled off the top, NOT a
    /// bookmark-row index. As the cursor moves down through the grid, the
    /// wordmark and URL bar collapse upward off the top of the screen.
    /// <para>
    /// When the URL bar is focused (<c>selectedIndex == -1</c>), the scroll
    /// offset is forced to <c>0</c> so the URL bar is always visible — the
    /// user can never type into a URL bar that has scrolled off-screen.
    /// </para>
    /// <para>
    /// When the cursor lands on the very first bookmark (index 0), the scroll
    /// offset is also reset to 0 so the wordmark reappears together with the
    /// URL bar — avoiding an awkward partial-header state.
    /// </para>
    /// </remarks>
    private static void AdjustLauncherScroll(CommandContext ctx, RenderOptions options)
    {
        var selectedIndex = ctx.NavigationService.LauncherSelectedIndex;

        // URL-bar focus → snap to top so the URL bar is in the viewport.
        if (selectedIndex < 0)
        {
            ctx.NavigationService.LauncherScrollOffset = 0;
            return;
        }

        // First bookmark → snap back to top so the wordmark reappears.
        if (selectedIndex == 0)
        {
            ctx.NavigationService.LauncherScrollOffset = 0;
            return;
        }

        var layout = ComputeVariantLayout(options);
        var headerPlusUrlBarLines = LauncherRenderer.ComputeHeaderPlusUrlBarLines(options.TerminalWidth, options.ShowSetupHint);
        var viewportHeight = LauncherRenderer.ComputeViewportHeight(options.TerminalHeight);

        // Page-line index of the top and bottom of the cell containing the selection.
        var selectedRow = selectedIndex / layout.Columns;
        var cellTopLine = headerPlusUrlBarLines + (selectedRow * layout.CellHeight);
        var cellBottomLine = cellTopLine + layout.CellHeight - 1;

        var currentOffset = ctx.NavigationService.LauncherScrollOffset;

        if (cellTopLine < currentOffset)
        {
            // Cell is above the viewport — scroll up so its top is the first visible line.
            ctx.NavigationService.LauncherScrollOffset = cellTopLine;
        }
        else if (cellBottomLine >= currentOffset + viewportHeight)
        {
            // Cell is below the viewport — scroll down so its bottom is the last visible line.
            ctx.NavigationService.LauncherScrollOffset = cellBottomLine - viewportHeight + 1;
        }
    }

    private static LauncherLayout ComputeVariantLayout(RenderOptions options)
    {
        return LauncherRenderer.ComputeLayout(
            options.TerminalWidth,
            options.TerminalHeight,
            options.LayoutVariant ?? "Grid",
            options.ShowSetupHint);
    }

    private static int GetLayoutColumns(RenderOptions options)
    {
        return ComputeVariantLayout(options).Columns;
    }
}
