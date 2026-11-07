// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Infrastructure.Browser.UI.Renderers;

/// <summary>
/// The launcher slot map (workspace-wyvga.3, spec S6). Two index spaces:
/// VIRTUAL indices address every selectable half/tile (digit-jump address);
/// CELL indices address grid cells, where the Reading List and Routines share
/// ONE cell (two stacked halves). With <c>bc</c> bookmarks, <c>rl = bc >= 1 ? 1 : 0</c>:
/// virtual 0..rl-1 = bookmark 0; virtual rl = Reading List (top half);
/// virtual rl+1 = Routines (bottom half); later virtuals = bookmarks (v-1 for
/// bc>=1, v>=3 → bookmark v-2). Virtual total = bc+2; cell total = bc+1.
/// </summary>
internal static class LauncherSlotMap
{
    /// <summary>Virtual index of the Reading List: 1 with bookmarks, 0 without.</summary>
    internal static int ReadingListIndex(int bookmarkCount) => bookmarkCount >= 1 ? 1 : 0;

    /// <summary>Virtual index of Routines (always directly after the Reading List).</summary>
    internal static int RoutinesIndex(int bookmarkCount) => ReadingListIndex(bookmarkCount) + 1;

    /// <summary>Number of virtual slots (selectable halves + bookmarks).</summary>
    internal static int VirtualTotal(int bookmarkCount) => bookmarkCount + 2;

    /// <summary>Number of grid cells (the split cell counts once).</summary>
    internal static int CellTotal(int bookmarkCount) => bookmarkCount + 1;

    internal static bool IsReadingList(int bookmarkCount, int virtualIndex) =>
        virtualIndex == ReadingListIndex(bookmarkCount);

    internal static bool IsRoutines(int bookmarkCount, int virtualIndex) =>
        virtualIndex == RoutinesIndex(bookmarkCount);

    /// <summary>Grid cell holding virtual index <paramref name="v"/> (negative stays negative).</summary>
    internal static int CellOf(int bookmarkCount, int v)
    {
        if (v < 0)
        {
            return v;
        }

        var rl = ReadingListIndex(bookmarkCount);
        if (v <= rl)
        {
            return v;
        }

        return v == rl + 1 ? rl : v - 1;
    }

    /// <summary>The first (top-half / only) virtual index of grid cell <paramref name="cell"/>.</summary>
    internal static int FirstVirtual(int bookmarkCount, int cell)
    {
        if (cell < 0)
        {
            return cell;
        }

        return cell <= ReadingListIndex(bookmarkCount) ? cell : cell + 1;
    }

    /// <summary>Bookmark index for a virtual index, or -1 for the Reading List / Routines slots.</summary>
    internal static int BookmarkIndexFromVirtual(int bookmarkCount, int v)
    {
        if (v < 0 || IsReadingList(bookmarkCount, v) || IsRoutines(bookmarkCount, v))
        {
            return -1;
        }

        return v == 0 ? 0 : v - 2;
    }

    /// <summary>Virtual index of bookmark <paramref name="bookmarkIndex"/>.</summary>
    internal static int VirtualIndexFromBookmark(int bookmarkIndex) =>
        bookmarkIndex <= 0 ? 0 : bookmarkIndex + 2;
}
