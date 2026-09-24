using BookHeaven.Core.DOM.Mini;

namespace BookHeaven.Core.DOM.CSS;

/// <summary>
/// A resolved style: the cascaded value (as a string) for each property. The layout
/// engine reads values via <see cref="GetPropertyValue"/> and parses lengths itself,
/// so this only needs to produce the winning raw value per property.
/// </summary>
public sealed class MiniStyle
{
    private readonly Dictionary<string, string> _props = new(StringComparer.OrdinalIgnoreCase);

    public string GetPropertyValue(string name) => _props.TryGetValue(name, out var v) ? v : string.Empty;

    internal void Set(string name, string value) => _props[name] = value;

    internal Dictionary<string, string> Props => _props;
}

/// <summary>
/// Computes an element's style by resolving the CSS cascade in-house: selector
/// matching, specificity, source order, <c>!important</c>, inheritance, and
/// <c>var()</c> resolution.
/// </summary>
public static class MiniStyleResolver
{
    /// <summary>Properties that inherit from the parent when not set on the element.</summary>
    private static readonly HashSet<string> Inheritable = new(StringComparer.OrdinalIgnoreCase)
    {
        "font-weight", "font-style", "font-size", "line-height",
        "letter-spacing", "word-spacing", "text-indent", "visibility",
    };

    /// <summary>
    /// Resolves the style for <paramref name="el"/> against the parsed rules, inheriting
    /// from <paramref name="parent"/> where applicable.
    /// </summary>
    public static MiniStyle Resolve(MiniElement el, List<MiniCssRule> rules, MiniStyle? parent)
    {
        var style = new MiniStyle();
        var best = new Dictionary<string, (int Importance, int Spec, int Order, string Value)>(StringComparer.OrdinalIgnoreCase);

        // Stylesheet rules.
        for (var r = 0; r < rules.Count; r++)
        {
            var rule = rules[r];
            if (!Matches(rule.Selector, el))
                continue;
            var spec = Specificity(rule.Selector);
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
            foreach (var (prop, _) in parent.Props)
                if (prop.StartsWith("--", StringComparison.Ordinal))
                    InheritIfAbsent(style, best, prop, parent);

            // The 'inherit' keyword: the declaration explicitly takes the parent's
            // value (already fully resolved, since styles resolve top-down).
            foreach (var (prop, value) in style.Props.ToList())
            {
                if (!string.Equals(value, "inherit", StringComparison.OrdinalIgnoreCase))
                    continue;
                var inherited = parent.GetPropertyValue(prop);
                if (inherited.Length > 0)
                    style.Set(prop, inherited);
                else
                    style.Props.Remove(prop);
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
            style.Set(prop, inherited);
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

    /// <summary>
    /// Splits a shorthand value on top-level whitespace (parentheses-aware) and
    /// repeats it per the CSS 1/2/3/4-value rules for the target longhand count.
    /// </summary>
    private static string[] SplitShorthandValues(string value, int count)
    {
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

        return count == 2
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
    private static int Specificity(MiniSelector selector)
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
    private static bool Matches(MiniSelector selector, MiniElement el)
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

    private static bool CompoundMatches(MiniCompound c, MiniElement el)
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

    private static bool AttributeMatches(MiniAttribute attr, MiniElement el)
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
        foreach (var (prop, value) in props.ToList())
        {
            if (!value.Contains("var(", StringComparison.Ordinal))
                continue;
            style.Set(prop, ResolveVarValue(value, props, depth: 0));
        }
    }

    private static string ResolveVarValue(string value, Dictionary<string, string> props, int depth)
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
            if (name.StartsWith("--", StringComparison.Ordinal) && props.TryGetValue(name, out var custom))
                replacement = ResolveVarValue(custom, props, depth + 1);
            else if (fallback is not null)
                replacement = ResolveVarValue(fallback, props, depth + 1);

            sb.Append(replacement);
            i = j + 1;
        }
        return sb.ToString().Trim();
    }
}
