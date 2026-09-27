namespace BookHeaven.Core.DOM.CSS.Models;

/// <summary>A parsed CSS rule: a selector plus its declarations.</summary>
public sealed class CssRule
{
    public required CssSelector Selector { get; init; }
    public required List<CssDeclaration> Declarations { get; init; }
}

/// <summary>A single declaration: property, raw value, and the <c>!important</c> flag.</summary>
public sealed record CssDeclaration(string Property, string Value, bool Important);

/// <summary>
/// A parsed selector: a list of compounds joined by combinators.
/// <see cref="Combinators"/> has <c>Compounds.Count - 1</c> entries; entry <c>i</c> is the
/// combinator between <c>Compounds[i]</c> and <c>Compounds[i+1]</c> (' ', '&gt;', '+' or '~'),
/// where ' ' is the descendant combinator.
/// </summary>
public sealed class CssSelector
{
    public required List<SelectorCompound> Compounds { get; init; }
    public char[] Combinators { get; init; } = [];
    /// <summary>True when the selector targets a <c>::first-letter</c> pseudo-element.</summary>
    public bool FirstLetter { get; init; }
}

/// <summary>An attribute selector: <c>[name]</c>, <c>[name=value]</c>, <c>[name~=value]</c>, ...</summary>
public sealed class CssAttribute
{
    public required string Name { get; init; }
    /// <summary>Comparison operator, or <c>null</c> for a bare presence test <c>[name]</c>.</summary>
    public string? Operator { get; init; }
    public string? Value { get; init; }
}

/// <summary>
/// A compound selector: an element name plus classes, ids, attribute selectors and
/// <c>:not()</c> negations. All positive parts must match; every <c>:not()</c> part must
/// NOT match.
/// </summary>
public sealed class SelectorCompound
{
    /// <summary>Element name, or <c>null</c> for the universal selector.</summary>
    public string? Element { get; init; }
    public List<string> Classes { get; init; } = [];
    public List<string> Ids { get; init; } = [];
    public List<CssAttribute> Attributes { get; init; } = [];
    /// <summary>Compounds from <c>:not(...)</c>; the element must match NONE of them.</summary>
    public List<SelectorCompound> Not { get; init; } = [];
    /// <summary>True when this compound is a <c>::first-letter</c> pseudo-element.</summary>
    public bool FirstLetter { get; init; }
}

/// <summary>
/// A resolved style: the cascaded value (as a string) for each property. The layout
/// engine reads values via <see cref="GetPropertyValue"/> and parses lengths itself,
/// so this only needs to produce the winning raw value per property.
///
/// Properties are a small fixed list (typically &lt; 20), read by name a handful of
/// times per element — a linear scan beats a Dictionary's per-entry overhead. The
/// style is also SHARED across every element with the same signature (the engine
/// memoizes resolutions per document), so it must stay immutable after resolution.
/// </summary>
public sealed class CssStyle
{
    // (name, value, explicit) triples. The explicit flag marks properties set by a
    // rule/inline style (as opposed to inherited): relative units in an inherited
    // value (e.g. an inherited `font-size: 2.42em`) must NOT be re-resolved against
    // the parent's resolved size — that would apply the unit twice.
    internal List<(string Name, string Value, bool Explicit)> Props { get; } = [];

    public string GetPropertyValue(string name)
    {
        var props = Props;
        for (var i = 0; i < props.Count; i++)
            if (string.Equals(props[i].Name, name, StringComparison.Ordinal))
                return props[i].Value;
        return string.Empty;
    }

    /// <summary>Sets a property as an EXPLICIT declaration (rule/inline style wins the cascade).</summary>
    internal void Set(string name, string value)
    {
        var props = Props;
        for (var i = 0; i < props.Count; i++)
        {
            if (string.Equals(props[i].Name, name, StringComparison.Ordinal))
            {
                var p = props[i];
                props[i] = (p.Name, value, true);
                return;
            }
        }
        props.Add((name, value, true));
    }

    /// <summary>Sets a property as INHERITED (the explicit flag is cleared, so relative units are not re-resolved).</summary>
    internal void SetInherited(string name, string value)
    {
        var props = Props;
        for (var i = 0; i < props.Count; i++)
        {
            if (string.Equals(props[i].Name, name, StringComparison.Ordinal))
            {
                props[i] = (name, value, false);
                return;
            }
        }
        props.Add((name, value, false));
    }

    /// <summary>True when <paramref name="name"/> was set by a rule/inline style, not inherited.</summary>
    internal bool IsExplicit(string name)
    {
        var props = Props;
        for (var i = 0; i < props.Count; i++)
            if (string.Equals(props[i].Name, name, StringComparison.Ordinal))
                return props[i].Explicit;
        return false;
    }

    internal void Remove(string name)
    {
        var props = Props;
        for (var i = 0; i < props.Count; i++)
            if (string.Equals(props[i].Name, name, StringComparison.Ordinal))
            {
                props.RemoveAt(i);
                return;
            }
    }
}