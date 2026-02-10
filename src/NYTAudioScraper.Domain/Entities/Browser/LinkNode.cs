// Educational and personal use only.

using NYTAudioScraper.Domain.Enums.Browser;
using NYTAudioScraper.Domain.ValueObjects.Browser;

namespace NYTAudioScraper.Domain.Entities.Browser;

/// <summary>
/// Represents a node in the hierarchical link tree.
/// Forms a tree structure with parent/child relationships.
/// </summary>
public class LinkNode
{
    /// <summary>
    /// Unique identifier for this node.
    /// </summary>
    public Guid Id { get; private set; } = Guid.NewGuid();

    /// <summary>
    /// Link information (URL, text, type, importance).
    /// </summary>
    public LinkInfo Link { get; private set; }

    /// <summary>
    /// Parent node in the tree (null for root).
    /// </summary>
    public LinkNode? Parent { get; private set; }

    /// <summary>
    /// Child nodes in the tree.
    /// </summary>
    public List<LinkNode> Children { get; private set; } = new();

    /// <summary>
    /// Depth in the tree (0 = root, 1 = first level, etc.).
    /// </summary>
    public int Depth { get; private set; }

    /// <summary>
    /// Current collapse state (Expanded or Collapsed).
    /// </summary>
    public NodeCollapseState CollapseState { get; private set; }

    /// <summary>
    /// Whether this node is currently selected (for keyboard navigation).
    /// </summary>
    public bool IsSelected { get; private set; }

    /// <summary>
    /// Whether this node is a group header (e.g., "Navigation", "Content").
    /// Group headers have special rendering and behavior (toggle collapse on Enter).
    /// </summary>
    public bool IsGroupHeader => Link.IsGroupHeader;

    private LinkNode(LinkInfo link, LinkNode? parent, int depth)
    {
        Link = link;
        Parent = parent;
        Depth = depth;
        CollapseState = link.ShouldStartCollapsed()
            ? NodeCollapseState.Collapsed
            : NodeCollapseState.Expanded;
    }

    /// <summary>
    /// Creates a root node for the navigation tree.
    /// </summary>
    public static LinkNode CreateRoot()
    {
        var rootLink = new LinkInfo
        {
            Url = string.Empty,
            DisplayText = "Root",
            Type = LinkType.Content,
            ImportanceScore = 100
        };

        return new LinkNode(rootLink, parent: null, depth: 0)
        {
            CollapseState = NodeCollapseState.Expanded
        };
    }

    /// <summary>
    /// Adds a child node to this node.
    /// </summary>
    public LinkNode AddChild(LinkInfo linkInfo)
    {
        var childNode = new LinkNode(linkInfo, parent: this, depth: Depth + 1);
        Children.Add(childNode);
        return childNode;
    }

    /// <summary>
    /// Expands this node to show children.
    /// </summary>
    public void Expand()
    {
        CollapseState = NodeCollapseState.Expanded;
    }

    /// <summary>
    /// Collapses this node to hide children.
    /// </summary>
    public void Collapse()
    {
        CollapseState = NodeCollapseState.Collapsed;
    }

    /// <summary>
    /// Toggles collapse state.
    /// </summary>
    public void ToggleCollapse()
    {
        CollapseState = CollapseState == NodeCollapseState.Expanded
            ? NodeCollapseState.Collapsed
            : NodeCollapseState.Expanded;
    }

    /// <summary>
    /// Marks this node as selected.
    /// </summary>
    public void Select()
    {
        IsSelected = true;
    }

    /// <summary>
    /// Marks this node as not selected.
    /// </summary>
    public void Deselect()
    {
        IsSelected = false;
    }

    /// <summary>
    /// Gets all visible descendant nodes (respects collapse state).
    /// </summary>
    public IEnumerable<LinkNode> GetVisibleDescendants()
    {
        if (CollapseState == NodeCollapseState.Collapsed)
        {
            // If collapsed, no descendants are visible
            yield break;
        }

        foreach (var child in Children)
        {
            yield return child;

            // Recursively get visible descendants
            foreach (var descendant in child.GetVisibleDescendants())
            {
                yield return descendant;
            }
        }
    }

    /// <summary>
    /// Gets all descendant nodes regardless of collapse state.
    /// </summary>
    public IEnumerable<LinkNode> GetAllDescendants()
    {
        foreach (var child in Children)
        {
            yield return child;

            foreach (var descendant in child.GetAllDescendants())
            {
                yield return descendant;
            }
        }
    }

    /// <summary>
    /// Counts total descendants (including collapsed).
    /// </summary>
    public int CountDescendants()
    {
        return Children.Sum(child => 1 + child.CountDescendants());
    }
}
