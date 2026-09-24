using System.Text;

namespace BookHeaven.Core.DOM.HTML.Models;

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

    // Attributes as a small pair array: most elements carry 0-2 attributes, so a
    // Dictionary would cost ~100 bytes of overhead per element for nothing.
    private (string Name, string Value)[] _attributes = [];

    /// <summary>Number of attributes on this element.</summary>
    public int AttributeCount => _attributes.Length;

    // Lazily cached: the tree is immutable after parsing, and the engine walks
    // Children several times per element — caching avoids an OfType iterator per call.
    private List<MiniElement>? _elementChildren;

    /// <summary>Element children only (text nodes excluded), matching AngleSharp's <c>Children</c>.</summary>
    public IEnumerable<MiniElement> Children
    {
        get
        {
            if (_elementChildren is { } cached) return cached;
            var list = new List<MiniElement>();
            foreach (var child in ChildNodes)
            {
                if (child is MiniElement element) list.Add(element);
            }
            _elementChildren = list;
            return _elementChildren;
        }
    }

    public MiniElement? ParentElement => Parent;

    public override MiniNodeType NodeType => MiniNodeType.Element;

    /// <summary>Value of the attribute with the given lowercase name, or null.</summary>
    public string? GetAttribute(string name)
    {
        for (var i = 0; i < _attributes.Length; i++)
        {
            if (string.Equals(_attributes[i].Name, name, StringComparison.Ordinal))
                return _attributes[i].Value;
        }
        return null;
    }

    /// <summary>True when the attribute with the given lowercase name is present.</summary>
    public bool HasAttribute(string name) => GetAttribute(name) is not null;

    /// <summary>Sets (or replaces) an attribute with a lowercase name.</summary>
    internal void SetAttribute(string name, string value)
    {
        for (var i = 0; i < _attributes.Length; i++)
        {
            if (string.Equals(_attributes[i].Name, name, StringComparison.Ordinal))
            {
                _attributes[i] = (name, value);
                return;
            }
        }
        var grown = new (string, string)[_attributes.Length + 1];
        Array.Copy(_attributes, grown, _attributes.Length);
        grown[_attributes.Length] = (name, value);
        _attributes = grown;
    }

    public string? Class => GetAttribute("class");
    public string? Id => GetAttribute("id");
    public string? Style => GetAttribute("style");

    // Lazily cached: the tree is immutable after parsing, and HasClass is invoked for
    // every (rule × element) pair during cascade matching — splitting the class
    // attribute on each call dominated the style phase. Split once, then reuse.
    private string[]? _classList;

    public bool HasClass(string cls)
    {
        var list = _classList;
        if (list is null)
        {
            var c = GetAttribute("class");
            if (c is null)
            {
                _classList = [];
                return false;
            }
            list = c.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            _classList = list;
        }
        return list.Contains(cls);
    }

    // Lazily cached: the tree is immutable after parsing, and the engine reads
    // TextContent several times per element (leaf measurement, inline runs, own-text
    // extraction). Computing it once removes the repeated O(subtree) StringBuilder
    // and string allocations from the layout hot path.
    private string? _textContent;

    /// <summary>Concatenated text of this element and all its descendants (computed once, then cached).</summary>
    public override string TextContent
    {
        get
        {
            if (_textContent is { } cached) return cached;
            var sb = new StringBuilder();
            AppendDescendantText(this, sb);
            _textContent = sb.ToString();
            return _textContent;
        }
    }

    // Lazily cached: the engine reads the trimmed text in several places (leaf
    // measurement, inline-run fallback, body root). Trim allocates a new string when
    // there is surrounding whitespace, so computing it once avoids the repeated copies.
    private string? _trimmedTextContent;

    /// <summary><see cref="TextContent"/> with surrounding whitespace removed (computed once, then cached).</summary>
    public string TrimmedTextContent
    {
        get
        {
            if (_trimmedTextContent is { } cached) return cached;
            _trimmedTextContent = TextContent.Trim();
            return _trimmedTextContent;
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
