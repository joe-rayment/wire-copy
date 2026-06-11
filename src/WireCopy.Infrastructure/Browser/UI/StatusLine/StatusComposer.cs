// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Infrastructure.Browser.UI.Renderers;

namespace WireCopy.Infrastructure.Browser.UI.StatusLine;

/// <summary>
/// Width-aware composer for the status line (workspace-wef6 B1). Given a width
/// budget and the items each channel wants to show, it decides what survives
/// and at which length, with hard guarantees:
///
/// <list type="bullet">
///   <item>An <see cref="StatusChannel.Alert"/> is NEVER dropped — it degrades
///   long→short and, at pathological widths, truncates, but always renders.</item>
///   <item>An unexpired <see cref="StatusChannel.Transient"/> survives every
///   re-render until its TTL (clock-based via the injected
///   <see cref="TimeProvider"/>) — transients are only dropped after every
///   ambient/activity item is gone and the left side is empty.</item>
///   <item>Hints absorb all squeeze first: they only render into space no
///   other channel claimed, choosing the largest tier that fits.</item>
///   <item>Per-item long→short degradation always happens before any drop,
///   least-important channel first.</item>
/// </list>
/// </summary>
internal sealed class StatusComposer
{
    /// <summary>Display width of the " · " separator between right-group items.</summary>
    private const int SeparatorWidth = 3;

    private readonly TimeProvider _clock;

    public StatusComposer(TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Composes the final line model for <paramref name="width"/> columns.
    /// </summary>
    /// <param name="width">Terminal width; the line uses at most width-1 columns.</param>
    /// <param name="left">Identity/context items rendered at the left edge, in order.</param>
    /// <param name="right">Status items (any channel); ordered by the composer as Alert → Transient → Activity → Ambient.</param>
    /// <param name="hints">Optional hint item whose variants are the hint TIERS — rendered into leftover space only.</param>
    /// <param name="help">Optional trailing help affordance; degrades with everything else but is dropped only before transient/alert truncation.</param>
    public StatusLineModel Compose(
        int width,
        IReadOnlyList<StatusItem> left,
        IReadOnlyList<StatusItem> right,
        StatusItem? hints = null,
        StatusItem? help = null)
    {
        var budget = Math.Max(0, width - 1);
        var now = _clock.GetUtcNow().UtcDateTime;

        var rightItems = right
            .Where(i => i.ExpiresAt == null || i.ExpiresAt > now)
            .OrderBy(i => (int)i.Channel)
            .ThenBy(i => i.Priority)
            .Select(i => new Placed(i))
            .ToList();
        var leftItems = left.Select(i => new Placed(i)).ToList();
        var helpItem = help != null ? new Placed(help) : null;

        // 1. Degrade right items long→short, least-important channel first
        //    (Ambient → Activity → Transient → Alert), then the help affordance.
        while (Used(leftItems, rightItems, helpItem) > budget && TryDegrade(rightItems, helpItem))
        {
            // Each TryDegrade call steps exactly one item one variant down.
        }

        // 2. Drop droppable items least-important first: Ambient, then Activity.
        //    Alerts and unexpired transients are not droppable here.
        while (Used(leftItems, rightItems, helpItem) > budget
               && TryDropLast(rightItems, StatusChannel.Ambient, StatusChannel.Activity))
        {
            // Each TryDropLast call removes exactly one item.
        }

        // 3. Squeeze the left identity group: truncate the last item, then drop
        //    items right-to-left.
        while (Used(leftItems, rightItems, helpItem) > budget && leftItems.Count > 0)
        {
            var over = Used(leftItems, rightItems, helpItem) - budget;
            var lastLeft = leftItems[^1];
            var lastWidth = lastLeft.Width;
            if (lastWidth - over >= 4)
            {
                lastLeft.TruncateTo(lastWidth - over);
                break;
            }

            leftItems.RemoveAt(leftItems.Count - 1);
        }

        // 4. Pathological widths: drop help, then transients (least important
        //    first), and as the last resort truncate the alert itself — an
        //    alert is never dropped outright.
        if (Used(leftItems, rightItems, helpItem) > budget)
        {
            helpItem = null;
        }

        while (Used(leftItems, rightItems, helpItem) > budget
               && TryDropLast(rightItems, StatusChannel.Transient))
        {
            // Transients only drop at pathological widths, least important first.
        }

        if (Used(leftItems, rightItems, helpItem) > budget && rightItems.Count > 0)
        {
            var over = Used(leftItems, rightItems, helpItem) - budget;
            rightItems[0].TruncateTo(Math.Max(1, rightItems[0].Width - over));
        }

        // 5. Hints fill whatever is left between the left group and the right
        //    group, choosing the largest tier that fits with one column of
        //    padding on each side.
        StatusSegment[]? chosenHints = null;
        if (hints != null)
        {
            var leftover = budget - Used(leftItems, rightItems, helpItem);
            foreach (var tier in hints.Variants)
            {
                var tierWidth = StatusLineModel.MeasureVariant(tier);
                if (tierWidth > 0 && tierWidth + 2 <= leftover)
                {
                    chosenHints = tier;
                    break;
                }
            }
        }

        // Final padding mirrors the painter's join rules exactly: left groups
        // (plus hints) joined by single spaces, right groups joined by " · ",
        // one space before help. The right block is right-aligned by pushing
        // it with padding; minimum one column when both blocks are non-empty.
        var leftBlockParts = leftItems.Count + (chosenHints != null ? 1 : 0);
        var leftBlockWidth = leftItems.Sum(i => i.Width)
                             + (chosenHints != null ? StatusLineModel.MeasureVariant(chosenHints) : 0)
                             + Math.Max(0, leftBlockParts - 1);
        var rightBlockWidth = rightItems.Sum(i => i.Width)
                              + (Math.Max(0, rightItems.Count - 1) * SeparatorWidth);
        if (helpItem != null)
        {
            rightBlockWidth += helpItem.Width + (rightItems.Count > 0 ? 1 : 0);
        }

        var minPadding = leftBlockWidth > 0 && rightBlockWidth > 0 ? 1 : 0;
        var padding = Math.Max(minPadding, budget - leftBlockWidth - rightBlockWidth);

        return new StatusLineModel
        {
            Left = leftItems.Select(i => i.Current).ToList(),
            Hints = chosenHints,
            Right = rightItems.Select(i => i.Current).ToList(),
            Help = helpItem?.Current,
            Padding = padding,
            Width = width,
        };
    }

    /// <summary>
    /// Total width if rendered now: left items joined by single spaces, one
    /// column of padding, right items joined by " · ", one space before help.
    /// </summary>
    private static int Used(List<Placed> left, List<Placed> right, Placed? help)
    {
        var leftW = left.Sum(i => i.Width) + Math.Max(0, left.Count - 1);
        var rightW = right.Sum(i => i.Width) + (Math.Max(0, right.Count - 1) * SeparatorWidth);
        var helpW = 0;
        if (help != null)
        {
            helpW = help.Width + (right.Count > 0 ? 1 : 0);
        }

        var paddingW = leftW > 0 && (rightW > 0 || helpW > 0) ? 1 : 0;
        return leftW + paddingW + rightW + helpW;
    }

    /// <summary>
    /// Degrades exactly one item one variant step, choosing the least important
    /// item that still has a shorter variant. Help degrades between Ambient and
    /// Activity (it's chrome, but more useful than a state badge). Returns false
    /// when nothing can degrade further.
    /// </summary>
    private static bool TryDegrade(List<Placed> right, Placed? help)
    {
        foreach (var channel in new[] { StatusChannel.Ambient, StatusChannel.Activity, StatusChannel.Transient, StatusChannel.Alert })
        {
            if (channel == StatusChannel.Activity && help != null && help.CanDegrade)
            {
                help.Degrade();
                return true;
            }

            for (var i = right.Count - 1; i >= 0; i--)
            {
                if (right[i].Item.Channel == channel && right[i].CanDegrade)
                {
                    right[i].Degrade();
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Drops the last (least important) item belonging to any of the given channels.</summary>
    private static bool TryDropLast(List<Placed> right, params StatusChannel[] channels)
    {
        for (var i = right.Count - 1; i >= 0; i--)
        {
            if (channels.Contains(right[i].Item.Channel))
            {
                right.RemoveAt(i);
                return true;
            }
        }

        return false;
    }

    /// <summary>Mutable placement state for one item during composition.</summary>
    private sealed class Placed
    {
        private StatusSegment[]? _truncated;

        public Placed(StatusItem item)
        {
            Item = item;
        }

        public StatusItem Item { get; }

        public int VariantIndex { get; private set; }

        public StatusSegment[] Current => _truncated ?? Item.Variants[VariantIndex];

        public int Width => StatusLineModel.MeasureVariant(Current);

        public bool CanDegrade => _truncated == null && VariantIndex < Item.Variants.Count - 1;

        public void Degrade() => VariantIndex++;

        /// <summary>Hard-truncates the current variant to a target display width (ellipsis included).</summary>
        public void TruncateTo(int targetWidth)
        {
            var remaining = targetWidth;
            var result = new List<StatusSegment>();
            foreach (var segment in Current)
            {
                var segWidth = RenderHelpers.GetDisplayWidth(segment.Text);
                if (segWidth <= remaining)
                {
                    result.Add(segment);
                    remaining -= segWidth;
                }
                else
                {
                    if (remaining > 0)
                    {
                        result.Add(segment with { Text = RenderHelpers.TruncateText(segment.Text, remaining) });
                    }

                    break;
                }
            }

            _truncated = result.ToArray();
        }
    }
}
