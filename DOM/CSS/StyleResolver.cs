using System.Collections.Concurrent;
using BookHeaven.Core.DOM.HTML.Models;

namespace BookHeaven.Core.DOM.CSS;

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
public sealed class MiniStyle
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

/// <summary>
/// Computes an element's style by resolving the CSS cascade in-house: selector
/// matching, specificity, source order, <c>!important</c>, inheritance, and
/// <c>var()</c> resolution.
/// </summary>
public static class StyleResolver
{
    /// <summary>Properties that inherit from the parent when not set on the element.</summary>
    private static readonly HashSet<string> Inheritable = new(StringComparer.OrdinalIgnoreCase)
    {
        "font-weight", "font-style", "font-size", "line-height",
        "letter-spacing", "word-spacing", "text-indent", "visibility",
    };

    /// <summary>
    /// Resolves the style for <paramref name="el"/> against the parsed rules, inheriting
    /// from <paramref name="parent"/> where applicable. <paramref name="specificities"/>
    /// is a parallel array of precomputed selector specificities (one per rule) — the
    /// engine computes it once per document instead of per (rule × element) pair.
    /// </summary>
    public static MiniStyle Resolve(Element el, List<MiniCssRule> rules, int[] specificities, MiniStyle? parent)
    {
        var style = new MiniStyle();
        // Pre-sized: a typical element matches a handful of declarations, so the
        // default-capacity dictionary would grow (and re-allocate) on every Consider.
        var best = new Dictionary<string, (int Importance, int Spec, int Order, string Value)>();

        // Stylesheet rules.
        for (var r = 0; r < rules.Count; r++)
        {
            var rule = rules[r];
            if (!Matches(rule.Selector, el))
                continue;
            var spec = specificities[r];
            foreach (var decl in rule.Declarations)
                ConsiderDecl(best, decl, decl.Important ? 1 : 0, spec, r);
        }

        // Inline style: highest specificity, after all stylesheet rules.
        var inline = el.Style;
        if (!string.IsNullOrWhiteSpace(inline))
        {
            var order = rules.Count;
            foreach (var decl in ParseInline(inline))
                ConsiderDecl(best, decl, decl.Important ? 1 : 0, InlineSpecificity, order);
        }

        foreach (var (prop, w) in best)
            style.Set(prop, w.Value);

        // Inheritance: inheritable properties and custom properties (--*) not set here.
        if (parent is not null)
        {
            foreach (var prop in Inheritable)
                InheritIfAbsent(style, best, prop, parent);
            var parentProps = parent.Props;
            for (var i = 0; i < parentProps.Count; i++)
            {
                var (prop, _, _) = parentProps[i];
                if (prop.StartsWith("--", StringComparison.Ordinal))
                    InheritIfAbsent(style, best, prop, parent);
            }

            // The 'inherit' keyword: the declaration explicitly takes the parent's
            // value (already fully resolved, since styles resolve top-down).
            var props = style.Props;
            for (var i = 0; i < props.Count; i++)
            {
                var (prop, value, _) = props[i];
                if (!string.Equals(value, "inherit", StringComparison.OrdinalIgnoreCase))
                    continue;
                var inherited = parent.GetPropertyValue(prop);
                if (inherited.Length > 0)
                {
                    // The value is now the parent's (possibly relative) string: treat
                    // it as inherited, not explicit, so units are not re-resolved.
                    style.SetInherited(prop, inherited);
                }
                else
                {
                    style.Remove(prop);
                    i--; // RemoveAt shifted the list; re-inspect the same index.
                }
            }
        }

        ResolveVars(style);
        return style;
    }

    private const int InlineSpecificity = 1_000_000;

    private static void InheritIfAbsent(MiniStyle style, Dictionary<string, (int, int, int, string)> best, string prop, MiniStyle parent)
    {
        if (best.ContainsKey(prop))
            return;
        var inherited = parent.GetPropertyValue(prop);
        if (inherited.Length > 0)
            // Inherited, NOT explicit: the raw value may carry relative units (e.g. an
            // inherited `font-size: 1.2em`) that must not be re-resolved against the
            // parent's already-resolved size, or the unit would apply twice per level.
            style.SetInherited(prop, inherited);
    }

    /// <summary>
    /// Box shorthands and logical properties expanded into physical longhands at cascade
    /// time. This replaces the old <c>ExpandLogicalMarginsInCss</c> pre-pass: the engine
    /// only ever reads physical longhands (margin-top, ...), so every logical form
    /// (margin-block, margin-block-start, margin-inline, ...) is mapped here.
    /// </summary>
    private static readonly Dictionary<string, string[]> BoxShorthands = new(StringComparer.OrdinalIgnoreCase)
    {
        ["margin"] = ["margin-top", "margin-right", "margin-bottom", "margin-left"],
        ["padding"] = ["padding-top", "padding-right", "padding-bottom", "padding-left"],
        ["margin-block"] = ["margin-top", "margin-bottom"],
        ["margin-inline"] = ["margin-left", "margin-right"],
        ["padding-block"] = ["padding-top", "padding-bottom"],
        ["padding-inline"] = ["padding-left", "padding-right"],
        ["margin-block-start"] = ["margin-top"],
        ["margin-block-end"] = ["margin-bottom"],
        ["margin-inline-start"] = ["margin-left"],
        ["margin-inline-end"] = ["margin-right"],
        ["padding-block-start"] = ["padding-top"],
        ["padding-block-end"] = ["padding-bottom"],
        ["padding-inline-start"] = ["padding-left"],
        ["padding-inline-end"] = ["padding-right"],
    };

    /// <summary>
    /// Feeds a declaration into the cascade, expanding box shorthands into their
    /// longhands so a later longhand declaration can override an earlier shorthand.
    /// </summary>
    private static void ConsiderDecl(
        Dictionary<string, (int Importance, int Spec, int Order, string Value)> best,
        MiniDeclaration decl, int importance, int spec, int order)
    {
        if (BoxShorthands.TryGetValue(decl.Property, out var longhands))
        {
            var values = SplitShorthandValues(decl.Value, longhands.Length);
            for (var k = 0; k < longhands.Length; k++)
                Consider(best, longhands[k], importance, spec, order, values[k]);
            return;
        }
        Consider(best, decl.Property, importance, spec, order, decl.Value);
    }

    // Shorthand values are pure functions of (value, count) and the same CSS values
    // (e.g. "1em 0") repeat across thousands of elements, so the split result is
    // memoized for the process lifetime instead of re-allocated per element.
    private static readonly ConcurrentDictionary<(string, int), string[]> ShorthandCache = [];

    /// <summary>
    /// Splits a shorthand value on top-level whitespace (parentheses-aware) and
    /// repeats it per the CSS 1/2/3/4-value rules for the target longhand count.
    /// </summary>
    private static string[] SplitShorthandValues(string value, int count)
    {
        var key = (value, count);
        if (ShorthandCache.TryGetValue(key, out var cached))
            return cached;

        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (depth == 0 && char.IsWhiteSpace(c))
            {
                if (i > start) parts.Add(value[start..i]);
                start = i + 1;
                while (start < value.Length && char.IsWhiteSpace(value[start])) start++;
            }
        }
        if (start < value.Length) parts.Add(value[start..]);

        string[] result = count == 2
            ? parts.Count switch
            {
                0 => ["", ""],
                1 => [parts[0], parts[0]],
                _ => [parts[0], parts[1]]
            }
            : parts.Count switch
            {
                0 => ["", "", "", ""],
                1 => [parts[0], parts[0], parts[0], parts[0]],
                2 => [parts[0], parts[1], parts[0], parts[1]],
                3 => [parts[0], parts[1], parts[2], parts[1]],
                _ => [parts[0], parts[1], parts[2], parts[3]]
            };
        ShorthandCache[key] = result;
        return result;
    }

    private static void Consider(
        Dictionary<string, (int Importance, int Spec, int Order, string Value)> best,
        string prop, int importance, int spec, int order, string value)
    {
        if (!best.TryGetValue(prop, out var cur))
        {
            best[prop] = (importance, spec, order, value);
            return;
        }
        if (importance > cur.Importance) { best[prop] = (importance, spec, order, value); return; }
        if (importance < cur.Importance) return;
        if (spec > cur.Spec) { best[prop] = (importance, spec, order, value); return; }
        if (spec == cur.Spec && order >= cur.Order) best[prop] = (importance, spec, order, value);
    }

    /// <summary>Approximate specificity: 10000 per id, 100 per class/attribute, 1 per element.</summary>
    public static int Specificity(MiniSelector selector)
    {
        var spec = 0;
        foreach (var c in selector.Compounds)
            spec += CompoundSpecificity(c);
        return spec;
    }

    private static int CompoundSpecificity(MiniCompound c)
    {
        var spec = c.Ids.Count * 10_000 + c.Classes.Count * 100 + (c.Element is null ? 0 : 1);
        spec += c.Attributes.Count * 100; // attribute selectors are class-level
        foreach (var n in c.Not)
            spec += CompoundSpecificity(n); // :not(S) carries the specificity of S
        return spec;
    }

    /// <summary>Matches a descendant selector: last compound on the element, earlier ones on ancestors.</summary>
    private static bool Matches(MiniSelector selector, Element el)
    {
        var compounds = selector.Compounds;
        if (!CompoundMatches(compounds[^1], el))
            return false;

        var ancestor = el.ParentElement;
        for (var i = compounds.Count - 2; i >= 0; i--)
        {
            var target = compounds[i];
            var found = false;
            while (ancestor is not null)
            {
                if (CompoundMatches(target, ancestor))
                {
                    found = true;
                    ancestor = ancestor.ParentElement;
                    break;
                }
                ancestor = ancestor.ParentElement;
            }
            if (!found)
                return false;
        }
        return true;
    }

    private static bool CompoundMatches(MiniCompound c, Element el)
    {
        if (c.Element is not null && !string.Equals(c.Element, el.TagName, StringComparison.OrdinalIgnoreCase))
            return false;
        foreach (var id in c.Ids)
            if (!string.Equals(id, el.Id, StringComparison.Ordinal))
                return false;
        foreach (var cls in c.Classes)
            if (!el.HasClass(cls))
                return false;
        foreach (var attr in c.Attributes)
            if (!AttributeMatches(attr, el))
                return false;
        foreach (var n in c.Not)
            if (CompoundMatches(n, el))
                return false; // :not() — the element must NOT match
        return true;
    }

    private static bool AttributeMatches(MiniAttribute attr, Element el)
    {
        var value = el.GetAttribute(attr.Name);
        if (attr.Operator is null)
            return value is not null; // bare presence test
        if (value is null)
            return false;
        var v = attr.Value ?? string.Empty;
        return attr.Operator switch
        {
            "=" => string.Equals(value, v, StringComparison.OrdinalIgnoreCase),
            "~=" => value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(v, StringComparer.OrdinalIgnoreCase),
            "^=" => value.StartsWith(v, StringComparison.OrdinalIgnoreCase),
            "$=" => value.EndsWith(v, StringComparison.OrdinalIgnoreCase),
            "*=" => value.Contains(v, StringComparison.OrdinalIgnoreCase),
            "|=" => string.Equals(value, v, StringComparison.OrdinalIgnoreCase) || value.StartsWith(v + "-", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    /// <summary>Parses an inline <c>style="a: b; c: d"</c> attribute into declarations.</summary>
    private static List<MiniDeclaration> ParseInline(string style)
    {
        var declarations = new List<MiniDeclaration>();
        foreach (var part in style.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = part.IndexOf(':');
            if (colon <= 0)
                continue;
            var property = part[..colon].Trim().ToLowerInvariant();
            var value = part[(colon + 1)..].Trim();
            var important = false;
            if (value.EndsWith("!important", StringComparison.OrdinalIgnoreCase))
            {
                important = true;
                value = value[..^10].Trim();
            }
            if (property.Length > 0 && value.Length > 0)
                declarations.Add(new MiniDeclaration(property, value, important));
        }
        return declarations;
    }

    /// <summary>Resolves <c>var(--name, fallback)</c> references against the element's custom properties.</summary>
    private static void ResolveVars(MiniStyle style)
    {
        var props = style.Props;
        for (var i = 0; i < props.Count; i++)
        {
            var (prop, value, explicitSet) = props[i];
            if (!value.Contains("var(", StringComparison.Ordinal))
                continue;
            props[i] = (prop, ResolveVarValue(value, props, depth: 0), explicitSet);
        }
    }

    // Memoizes top-level var() resolution. The result is a pure function of the raw
    // value plus the values of the custom properties in scope; those are inherited and
    // rarely overridden, so the same key repeats across the tree and the expensive
    // StringBuilder resolution runs once per distinct key instead of once per element.
    // The key is a (value, fingerprint) tuple — a struct that references the shared
    // value string and a 32-bit hash of the in-scope custom props, so building it
    // allocates nothing.
    private static readonly ConcurrentDictionary<(string, int), string> VarCache = [];

    private static string ResolveVarValue(string value, List<(string Name, string Value, bool Explicit)> props, int depth)
    {
        if (depth > 0)
            return ResolveVarValueCore(value, props, depth);

        var key = (value, CustomFingerprint(props));
        if (VarCache.TryGetValue(key, out var cached))
            return cached;
        var resolved = ResolveVarValueCore(value, props, 0);
        VarCache[key] = resolved;
        return resolved;
    }

    // 32-bit fingerprint of the custom properties in scope. The resolved value depends
    // only on `value` plus these, so hashing them (no allocation) lets elements sharing
    // the same in-scope config reuse one cache entry. A 32-bit collision across the few
    // distinct configs in a book is negligible.
    private static int CustomFingerprint(List<(string Name, string Value, bool Explicit)> props)
    {
        var hash = new HashCode();
        for (var i = 0; i < props.Count; i++)
        {
            var (name, v, _) = props[i];
            if (!name.StartsWith("--", StringComparison.Ordinal))
                continue;
            hash.Add(name);
            hash.Add(v);
        }
        return hash.ToHashCode();
    }

    private static string ResolveVarValueCore(string value, List<(string Name, string Value, bool Explicit)> props, int depth)
    {
        if (depth > 8 || !value.Contains("var(", StringComparison.Ordinal))
            return value;

        var sb = new System.Text.StringBuilder(value.Length);
        var i = 0;
        while (i < value.Length)
        {
            var start = value.IndexOf("var(", i, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                sb.Append(value, i, value.Length - i);
                break;
            }
            sb.Append(value, i, start - i);

            // Find the matching close paren, tracking nesting.
            var depthParen = 1;
            var j = start + 4;
            var comma = -1;
            for (; j < value.Length; j++)
            {
                var c = value[j];
                if (c == '(') depthParen++;
                else if (c == ')')
                {
                    depthParen--;
                    if (depthParen == 0) break;
                }
                else if (c == ',' && depthParen == 1 && comma < 0)
                    comma = j;
            }
            if (j >= value.Length)
            {
                sb.Append(value[start..]);
                break;
            }

            var inner = value[(start + 4)..j];
            var name = comma < 0 ? inner : inner[..comma];
            var fallback = comma < 0 ? null : inner[(comma + 1)..].Trim();
            name = name.Trim();

            var replacement = string.Empty;
            if (name.StartsWith("--", StringComparison.Ordinal) && FindCustom(props, name) is { } custom)
                replacement = ResolveVarValueCore(custom, props, depth + 1);
            else if (fallback is not null)
                replacement = ResolveVarValueCore(fallback, props, depth + 1);

            sb.Append(replacement);
            i = j + 1;
        }
        return sb.ToString().Trim();
    }

    private static string? FindCustom(List<(string Name, string Value, bool Explicit)> props, string name)
    {
        for (var i = 0; i < props.Count; i++)
        {
            var (n, v, _) = props[i];
            if (string.Equals(n, name, StringComparison.Ordinal))
                return v;
        }
        return null;
    }
}
