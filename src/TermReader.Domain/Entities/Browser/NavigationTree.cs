// Licensed under the MIT License. See LICENSE in the repository root.

using TermReader.Domain.Enums.Browser;
using TermReader.Domain.ValueObjects.Browser;

namespace TermReader.Domain.Entities.Browser;

/// <summary>
/// Represents the complete hierarchical link tree for a page.
/// Manages navigation state and selection.
/// </summary>
public class NavigationTree
{
    /// <summary>
    /// Root node of the tree.
    /// </summary>
    public LinkNode Root { get; private set; }

    /// <summary>
    /// Currently selected node (for keyboard navigation).
    /// </summary>
    public LinkNode? CurrentSelection { get; private set; }

    /// <summary>
    /// Total number of links in the tree (excluding root).
    /// </summary>
    public int TotalLinks { get; private set; }

    /// <summary>
    /// IDs of nodes toggled for batch operations (separate from cursor selection).
    /// </summary>
    public HashSet<Guid> SelectedNodeIds { get; } = new();

    /// <summary>
    /// Number of nodes currently toggled for batch operations.
    /// </summary>
    public int SelectionCount => SelectedNodeIds.Count;

    private List<LinkNode>? _cachedVisibleNodes;

    private NavigationTree(LinkNode root)
    {
        Root = root;
        TotalLinks = root.CountDescendants();

        // Select first child by default
        CurrentSelection = root.Children.FirstOrDefault();
        CurrentSelection?.Select();
    }

    /// <summary>
    /// Builds a navigation tree from a list of links (flat structure).
    /// </summary>
    public static NavigationTree Build(List<LinkInfo> links)
    {
        var root = LinkNode.CreateRoot();

        // For now, add all links as direct children of root
        // Future: implement smart hierarchy based on DOM structure
        foreach (var link in links)
        {
            root.AddChild(link);
        }

        return new NavigationTree(root);
    }

    /// <summary>
    /// Builds a navigation tree with hierarchical groups.
    /// Content links are added directly under root (no group header).
    /// Navigation, External, and Footer get collapsible group headers (collapsed by default).
    /// </summary>
    public static NavigationTree BuildWithGroups(Dictionary<LinkType, List<LinkInfo>> groupedLinks)
    {
        var root = LinkNode.CreateRoot();

        // Define the order of groups: Content first (most important), then others
        var groupOrder = new[] { LinkType.Content, LinkType.Navigation, LinkType.External, LinkType.Footer };

        foreach (var linkType in groupOrder)
        {
            if (!groupedLinks.TryGetValue(linkType, out var links) || links.Count == 0)
            {
                continue;
            }

            if (linkType == LinkType.Content)
            {
                // Content links may be sub-grouped by SectionTitle
                AddContentLinks(root, links);
            }
            else
            {
                // Non-content groups get a collapsible group header
                var groupHeader = LinkInfo.CreateGroupHeader(linkType);
                var groupNode = root.AddChild(groupHeader);

                foreach (var link in links)
                {
                    groupNode.AddChild(link);
                }
            }
        }

        return new NavigationTree(root);
    }

    /// <summary>
    /// Builds a navigation tree from an already-constructed root node.
    /// Used when the tree structure has been built externally (e.g., from AI hierarchy config).
    /// </summary>
    public static NavigationTree BuildFromRoot(LinkNode root)
    {
        return new NavigationTree(root);
    }

    /// <summary>
    /// Adds content links to root, optionally sub-grouped by SectionTitle.
    /// Sub-sections are created when there are 2+ distinct non-null SectionTitle values
    /// and 2+ links per section on average. Links without a SectionTitle are placed
    /// directly under root (headerless / featured flow). Sections with the same title
    /// are merged, preserving the order of each section's first occurrence.
    /// </summary>
    private static void AddContentLinks(LinkNode root, List<LinkInfo> links)
    {
        if (!ShouldSubGroup(links))
        {
            // No sub-grouping: add all links directly under root
            foreach (var link in links)
            {
                root.AddChild(link);
            }

            return;
        }

        // Pre-group links by SectionTitle, preserving first-occurrence order.
        // Links with null SectionTitle go directly under root (featured/front page).
        var sectionOrder = new List<string>();
        var sectionLinks = new Dictionary<string, List<LinkInfo>>(StringComparer.OrdinalIgnoreCase);
        var headerlessLinks = new List<LinkInfo>();

        foreach (var link in links)
        {
            if (link.SectionTitle == null)
            {
                headerlessLinks.Add(link);
            }
            else
            {
                if (!sectionLinks.TryGetValue(link.SectionTitle, out var sectionList))
                {
                    sectionList = new List<LinkInfo>();
                    sectionLinks[link.SectionTitle] = sectionList;
                    sectionOrder.Add(link.SectionTitle);
                }

                sectionList.Add(link);
            }
        }

        // Add headerless links first (featured / front page articles)
        foreach (var link in headerlessLinks)
        {
            root.AddChild(link);
        }

        // Add each section with a single header, in first-occurrence order
        foreach (var sectionTitle in sectionOrder)
        {
            var sectionHeader = LinkInfo.CreateSubSectionHeader(sectionTitle, LinkType.Content);
            var sectionNode = root.AddChild(sectionHeader);

            foreach (var link in sectionLinks[sectionTitle])
            {
                sectionNode.AddChild(link);
            }
        }
    }

    /// <summary>
    /// Determines whether content links should be sub-grouped by SectionTitle.
    /// Requires 2+ distinct non-null SectionTitle values and 2+ links per section on average.
    /// </summary>
    private static bool ShouldSubGroup(List<LinkInfo> links)
    {
        var distinctSections = links
            .Where(l => l.SectionTitle != null)
            .Select(l => l.SectionTitle)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (distinctSections.Count < 2)
        {
            return false;
        }

        var linksWithSection = links.Count(l => l.SectionTitle != null);
        var avgPerSection = (double)linksWithSection / distinctSections.Count;

        return avgPerSection >= 2.0;
    }

    /// <summary>
    /// Selects the next visible node in the tree.
    /// </summary>
    public void SelectNext()
    {
        if (CurrentSelection == null)
            return;

        var visibleNodes = GetCachedVisibleNodes();
        var currentIndex = visibleNodes.IndexOf(CurrentSelection);

        if (currentIndex >= 0 && currentIndex < visibleNodes.Count - 1)
        {
            CurrentSelection.Deselect();
            CurrentSelection = visibleNodes[currentIndex + 1];
            CurrentSelection.Select();
        }
    }

    /// <summary>
    /// Selects the previous visible node in the tree.
    /// </summary>
    public void SelectPrevious()
    {
        if (CurrentSelection == null)
            return;

        var visibleNodes = GetCachedVisibleNodes();
        var currentIndex = visibleNodes.IndexOf(CurrentSelection);

        if (currentIndex > 0)
        {
            CurrentSelection.Deselect();
            CurrentSelection = visibleNodes[currentIndex - 1];
            CurrentSelection.Select();
        }
    }

    /// <summary>
    /// Selects the parent of the current node.
    /// </summary>
    public void SelectParent()
    {
        if (CurrentSelection?.Parent != null && CurrentSelection.Parent != Root)
        {
            CurrentSelection.Deselect();
            CurrentSelection = CurrentSelection.Parent;
            CurrentSelection.Select();
        }
    }

    /// <summary>
    /// Selects the first child of the current node (if expanded).
    /// </summary>
    public void SelectFirstChild()
    {
        if (CurrentSelection?.Children.Any() == true &&
            CurrentSelection.CollapseState == Enums.Browser.NodeCollapseState.Expanded)
        {
            CurrentSelection.Deselect();
            CurrentSelection = CurrentSelection.Children.First();
            CurrentSelection.Select();
        }
    }

    /// <summary>
    /// Toggles collapse state of current selection.
    /// </summary>
    public void ToggleCollapse()
    {
        CurrentSelection?.ToggleCollapse();
        InvalidateVisibleNodeCache();
    }

    /// <summary>
    /// Expands the current selection.
    /// </summary>
    public void Expand()
    {
        CurrentSelection?.Expand();
        InvalidateVisibleNodeCache();
    }

    /// <summary>
    /// Collapses the current selection.
    /// </summary>
    public void Collapse()
    {
        CurrentSelection?.Collapse();
        InvalidateVisibleNodeCache();
    }

    /// <summary>
    /// Gets the currently selected node.
    /// </summary>
    public LinkNode? GetSelectedNode() => CurrentSelection;

    /// <summary>
    /// Gets all visible nodes (respects collapse state).
    /// </summary>
    public IEnumerable<LinkNode> GetVisibleNodes()
    {
        return GetCachedVisibleNodes();
    }

    /// <summary>
    /// Gets all nodes regardless of collapse state.
    /// </summary>
    public IEnumerable<LinkNode> GetAllNodes()
    {
        return Root.GetAllDescendants();
    }

    /// <summary>
    /// Selects a specific node by its ID.
    /// </summary>
    public bool SelectNodeById(Guid nodeId)
    {
        var node = GetAllNodes().FirstOrDefault(n => n.Id == nodeId);

        if (node != null)
        {
            CurrentSelection?.Deselect();
            CurrentSelection = node;
            CurrentSelection.Select();
            return true;
        }

        return false;
    }

    /// <summary>
    /// Ensures a node is selected. If no node is currently selected,
    /// selects the first visible node. Returns true if a selection exists.
    /// </summary>
    public bool EnsureSelection()
    {
        // If already have a valid selection, nothing to do
        if (CurrentSelection != null && CurrentSelection.IsSelected)
        {
            return true;
        }

        // Try to select the first visible node
        var firstVisible = GetVisibleNodes().FirstOrDefault();
        if (firstVisible != null)
        {
            CurrentSelection?.Deselect();
            CurrentSelection = firstVisible;
            CurrentSelection.Select();
            return true;
        }

        return false;
    }

    /// <summary>
    /// Expands all nodes in the tree.
    /// </summary>
    public void ExpandAll()
    {
        foreach (var node in GetAllNodes())
        {
            node.Expand();
        }

        InvalidateVisibleNodeCache();
    }

    /// <summary>
    /// Collapses all nodes in the tree.
    /// </summary>
    public void CollapseAll()
    {
        foreach (var node in GetAllNodes())
        {
            node.Collapse();
        }

        InvalidateVisibleNodeCache();
    }

    /// <summary>
    /// Toggles multi-select on the current node. If it's a group header,
    /// toggles all its non-header children. Returns the number of items affected.
    /// </summary>
    public int ToggleCurrentNodeSelection()
    {
        if (CurrentSelection == null)
        {
            return 0;
        }

        if (CurrentSelection.IsGroupHeader)
        {
            return ToggleSectionSelection(CurrentSelection);
        }

        return ToggleNodeSelection(CurrentSelection);
    }

    /// <summary>
    /// Toggles a single node in the selection set.
    /// </summary>
    public int ToggleNodeSelection(LinkNode node)
    {
        if (node.IsGroupHeader || string.IsNullOrEmpty(node.Link.Url))
        {
            return 0;
        }

        if (!SelectedNodeIds.Remove(node.Id))
        {
            SelectedNodeIds.Add(node.Id);
        }

        return 1;
    }

    /// <summary>
    /// Toggles all non-header children of a group header.
    /// If all children are selected, deselects all. Otherwise, selects all.
    /// </summary>
    public int ToggleSectionSelection(LinkNode header)
    {
        var children = header.Children
            .Where(c => !c.IsGroupHeader && !string.IsNullOrEmpty(c.Link.Url))
            .ToList();

        if (children.Count == 0)
        {
            return 0;
        }

        var allSelected = children.All(c => SelectedNodeIds.Contains(c.Id));

        if (allSelected)
        {
            foreach (var child in children)
            {
                SelectedNodeIds.Remove(child.Id);
            }
        }
        else
        {
            foreach (var child in children)
            {
                SelectedNodeIds.Add(child.Id);
            }
        }

        return children.Count;
    }

    /// <summary>
    /// Returns all nodes currently in the multi-select set.
    /// </summary>
    public IReadOnlyList<LinkNode> GetSelectedNodes()
    {
        if (SelectedNodeIds.Count == 0)
        {
            return [];
        }

        return GetAllNodes()
            .Where(n => SelectedNodeIds.Contains(n.Id))
            .ToList();
    }

    /// <summary>
    /// Checks whether a node is in the multi-select set.
    /// </summary>
    public bool IsNodeSelected(LinkNode node) => SelectedNodeIds.Contains(node.Id);

    /// <summary>
    /// Checks whether all non-header children of a group header are selected.
    /// </summary>
    public bool IsSectionFullySelected(LinkNode header)
    {
        return header.IsGroupHeader &&
               header.Children.Count > 0 &&
               header.Children
                   .Where(c => !c.IsGroupHeader && !string.IsNullOrEmpty(c.Link.Url))
                   .All(c => SelectedNodeIds.Contains(c.Id));
    }

    /// <summary>
    /// Checks whether any (but not all) children of a group header are selected.
    /// </summary>
    public bool IsSectionPartiallySelected(LinkNode header)
    {
        if (!header.IsGroupHeader || header.Children.Count == 0)
        {
            return false;
        }

        var selectableChildren = header.Children
            .Where(c => !c.IsGroupHeader && !string.IsNullOrEmpty(c.Link.Url))
            .ToList();

        var selectedCount = selectableChildren.Count(c => SelectedNodeIds.Contains(c.Id));
        return selectedCount > 0 && selectedCount < selectableChildren.Count;
    }

    /// <summary>
    /// Clears all multi-select state.
    /// </summary>
    public void ClearSelection() => SelectedNodeIds.Clear();

    /// <summary>
    /// Invalidates the cached visible node list. Call after any operation
    /// that changes node collapse state outside NavigationTree methods.
    /// </summary>
    public void InvalidateVisibleNodeCache() => _cachedVisibleNodes = null;

    private List<LinkNode> GetCachedVisibleNodes()
    {
        return _cachedVisibleNodes ??= Root.GetVisibleDescendants().ToList();
    }
}
