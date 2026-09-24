using System.Text;

namespace BookHeaven.Core.DOM.Mini;

/// <summary>
/// Node kinds of the lightweight DOM. Only the two the layout engine distinguishes:
/// text (measured) and element (block/inline structure). Comments, DOCTYPE and
/// processing instructions are dropped by the parser and never appear here.
/// </summary>
public enum MiniNodeType
{
    Text,
    Element,
}

/// <summary>
/// Base node of the lightweight DOM tree produced by <see cref="MiniHtmlParser"/>.
/// A deliberately minimal replacement for AngleSharp's node model: just enough for the
/// block layout engine (tree shape, tag, attributes, text) with none of the HTML5
/// tree-construction, namespace or error-recovery machinery.
/// </summary>
public abstract class MiniNode
{
    /// <summary>Parent element, or null for the document body root.</summary>
    public MiniElement? Parent { get; internal set; }

    public abstract MiniNodeType NodeType { get; }

    /// <summary>Full text content of this node and its descendants (text nodes verbatim).</summary>
    public abstract string TextContent { get; }
}

/// <summary>A run of character data.</summary>
public sealed class MiniText : MiniNode
{
    public MiniText(string value) => Value = value;

    /// <summary>The raw character data (entities already decoded by the parser).</summary>
    public string Value { get; }

    public override MiniNodeType NodeType => MiniNodeType.Text;
    public override string TextContent => Value;
}

/// <summary>
/// An element: tag name, a lowercase-keyed attribute map and an ordered child list.
/// Mirrors the exact members the layout engine reads from AngleSharp's <c>IElement</c>
/// so the engine can be pointed at this tree with minimal changes.
/// </summary>
public sealed class MiniElement : MiniNode
{
    public MiniElement(string tagName)
    {
        TagName = tagName;
        ChildNodes = [];
    }

    /// <summary>Lowercase tag name (e.g. "p", "div", "img").</summary>
    public string TagName { get; }

    /// <summary>All child nodes (text and element) in document order.</summary>
    public List<MiniNode> ChildNodes { get; }

    /// <summary>Attributes with lowercase names and decoded values.</summary>
    public Dictionary<string, string> Attributes { get; } = [];

    /// <summary>Element children only (text nodes excluded), matching AngleSharp's <c>Children</c>.</summary>
    public IEnumerable<MiniElement> Children => ChildNodes.OfType<MiniElement>();

    public MiniElement? ParentElement => Parent;

    public override MiniNodeType NodeType => MiniNodeType.Element;

    public string? GetAttribute(string name)
        => Attributes.TryGetValue(name, out var v) ? v : null;

    public string? Class => GetAttribute("class");
    public string? Id => GetAttribute("id");
    public string? Style => GetAttribute("style");

    public bool HasClass(string cls)
    {
        var c = GetAttribute("class");
        if (c is null) return false;
        return c.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Contains(cls);
    }

    /// <summary>Concatenated text of this element and all its descendants.</summary>
    public override string TextContent
    {
        get
        {
            var sb = new StringBuilder();
            AppendDescendantText(this, sb);
            return sb.ToString();
        }
    }

    private static void AppendDescendantText(MiniNode node, StringBuilder sb)
    {
        switch (node)
        {
            case MiniText t:
                sb.Append(t.Value);
                break;
            case MiniElement e:
                foreach (var child in e.ChildNodes)
                    AppendDescendantText(child, sb);
                break;
        }
    }
}

/// <summary>The parsed document: a single body element (the fragment's root).</summary>
public sealed class MiniDocument(MiniElement html)
{
    /// <summary>The synthetic <c>html</c> root that wraps head and body.</summary>
    public MiniElement Root => html;

    public MiniElement? Head => html.Children.FirstOrDefault(e => e.TagName == "head");
    public MiniElement? Body => html.Children.FirstOrDefault(e => e.TagName == "body");
}
