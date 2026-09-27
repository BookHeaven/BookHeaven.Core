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