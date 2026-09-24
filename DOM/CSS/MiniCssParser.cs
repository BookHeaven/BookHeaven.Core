namespace BookHeaven.Core.DOM.CSS;

/// <summary>A parsed CSS rule: a selector plus its declarations.</summary>
public sealed class MiniCssRule
{
    public required MiniSelector Selector { get; init; }
    public required List<MiniDeclaration> Declarations { get; init; }
}

/// <summary>A single declaration: property, raw value, and the <c>!important</c> flag.</summary>
public sealed record MiniDeclaration(string Property, string Value, bool Important);

/// <summary>A parsed selector: a list of compounds joined by descendant combinators.</summary>
public sealed class MiniSelector
{
    public required List<MiniCompound> Compounds { get; init; }
}

/// <summary>An attribute selector: <c>[name]</c>, <c>[name=value]</c>, <c>[name~=value]</c>, ...</summary>
public sealed class MiniAttribute
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
public sealed class MiniCompound
{
    /// <summary>Element name, or <c>null</c> for the universal selector.</summary>
    public string? Element { get; init; }
    public List<string> Classes { get; init; } = [];
    public List<string> Ids { get; init; } = [];
    public List<MiniAttribute> Attributes { get; init; } = [];
    /// <summary>Compounds from <c>:not(...)</c>; the element must match NONE of them.</summary>
    public List<MiniCompound> Not { get; init; } = [];
}

/// <summary>
/// A minimal CSS parser for ebook styling. Parses rules, simple selectors
/// (element / class / id / universal / compound / descendant / comma), and
/// declarations with <c>!important</c>. Flattens <c>@media</c> blocks and skips
/// at-rules that do not affect layout (<c>@font-face</c>, <c>@keyframes</c>, ...).
/// </summary>
public static class MiniCssParser
{
    public static List<MiniCssRule> Parse(string css)
    {
        var rules = new List<MiniCssRule>();
        if (string.IsNullOrWhiteSpace(css))
            return rules;

        var text = StripComments(css);
        ParseBlock(text, 0, text.Length, rules, inMedia: false);
        return rules;
    }

    /// <summary>Strips <c>/* ... */</c> comments.</summary>
    private static string StripComments(string css)
    {
        var result = new System.Text.StringBuilder(css.Length);
        var i = 0;
        while (i < css.Length)
        {
            if (i + 1 < css.Length && css[i] == '/' && css[i + 1] == '*')
            {
                var end = css.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? css.Length : end + 2;
                result.Append(' ');
                continue;
            }
            result.Append(css[i]);
            i++;
        }
        return result.ToString();
    }

    /// <summary>
    /// Parses rules within <c>text[start..end)</c>. When inside a <c>@media</c>
    /// block, nested rules are flattened into the same list.
    /// </summary>
    private static void ParseBlock(string text, int start, int end, List<MiniCssRule> rules, bool inMedia)
    {
        var i = start;
        while (i < end)
        {
            // Skip whitespace.
            while (i < end && char.IsWhiteSpace(text[i])) i++;
            if (i >= end) break;

            var openRel = text.AsSpan(i, end - i).IndexOf('{');
            if (openRel < 0) break;
            var open = i + openRel;

            var prelude = text.AsSpan(i, open - i).Trim().ToString();
            var close = FindMatchingBrace(text, open, end);
            var bodyEnd = close < 0 ? end : close;

            if (prelude.Length > 0 && prelude[0] == '@')
            {
                var atName = AtRuleName(prelude);
                if (atName is "media")
                {
                    // Only flatten media queries that match the rendering device
                    // (screen). Unknown types (e.g. `amzn-mobi`) are ignored,
                    // matching AngleSharp's behavior.
                    var mediaList = prelude["@media".Length..].Trim();
                    if (MediaMatchesScreen(mediaList))
                        ParseBlock(text, open + 1, bodyEnd, rules, inMedia: true);
                }
                // Other at-rules (@font-face, @keyframes, @page, ...) are skipped.
            }
            else
            {
                ParseRule(prelude, text, open + 1, bodyEnd, rules);
            }

            i = close < 0 ? end : close + 1;
        }
    }

    private static string AtRuleName(string prelude)
    {
        var name = prelude[1..];
        var space = name.IndexOfAny([' ', '\t', '\r', '\n', '(']);
        return (space < 0 ? name : name[..space]).ToLowerInvariant();
    }

    /// <summary>
    /// True when every expression in the media list matches a screen device:
    /// feature queries in parentheses are assumed to match; a type keyword must
    /// be <c>screen</c> or <c>all</c>.
    /// </summary>
    private static bool MediaMatchesScreen(string mediaList)
    {
        if (mediaList.Length == 0) return true;
        foreach (var expr in SplitTopLevel(mediaList, ','))
        {
            var e = expr.Trim();
            if (e.Length == 0) continue;
            if (e.StartsWith('(')) continue;
            var type = e.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0].ToLowerInvariant();
            if (type is not ("screen" or "all"))
                return false;
        }
        return true;
    }

    private static void ParseRule(string selectorText, string text, int bodyStart, int bodyEnd, List<MiniCssRule> rules)
    {
        var declarations = ParseDeclarations(text, bodyStart, bodyEnd);
        if (declarations.Count == 0)
            return;

        // A selector list may contain comma-separated selectors.
        foreach (var part in SplitTopLevel(selectorText, ','))
        {
            var selector = ParseSelector(part.Trim());
            if (selector is null)
                continue;
            rules.Add(new MiniCssRule { Selector = selector, Declarations = declarations });
        }
    }

    /// <summary>Parses the declaration block body into declarations.</summary>
    private static List<MiniDeclaration> ParseDeclarations(string text, int start, int end)
    {
        var declarations = new List<MiniDeclaration>();
        var i = start;
        while (i < end)
        {
            var semiRel = text.AsSpan(i, end - i).IndexOf(';');
            var semi = semiRel < 0 ? -1 : i + semiRel;
            var declEnd = semi < 0 ? end : semi;
            var declText = text.AsSpan(i, declEnd - i).Trim().ToString();
            i = semi < 0 ? end : semi + 1;
            if (declText.Length == 0)
                continue;

            var colon = declText.IndexOf(':');
            if (colon <= 0)
                continue;

            var property = declText[..colon].Trim().ToLowerInvariant();
            var value = declText[(colon + 1)..].Trim();
            var important = false;
            if (value.EndsWith("!important", StringComparison.OrdinalIgnoreCase))
            {
                important = true;
                value = value[..^10].Trim();
            }
            if (property.Length == 0 || value.Length == 0)
                continue;
            declarations.Add(new MiniDeclaration(property, value, important));
        }
        return declarations;
    }

    /// <summary>
    /// Parses a single selector into compounds. Returns <c>null</c> if the
    /// selector uses combinators/pseudos this parser does not support.
    /// </summary>
    private static MiniSelector? ParseSelector(string selector)
    {
        if (selector.Length == 0)
            return null;

        var compounds = new List<MiniCompound>();
        foreach (var token in selector.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var compound = ParseCompound(token);
            if (compound is null)
                return null;
            compounds.Add(compound);
        }
        return compounds.Count > 0 ? new MiniSelector { Compounds = compounds } : null;
    }

    /// <summary>
    /// Parses a compound selector like <c>p.foo#bar</c>, <c>*</c>, <c>p:not([class])</c>
    /// or <c>[class]</c>. Returns <c>null</c> for syntax this parser does not support.
    /// </summary>
    private static MiniCompound? ParseCompound(string token)
    {
        if (token.Length == 0)
            return null;

        var element = (string?)null;
        var classes = new List<string>();
        var ids = new List<string>();
        var attributes = new List<MiniAttribute>();
        var nots = new List<MiniCompound>();

        var i = 0;
        // Element name (or universal '*'). A leading '[', ':' or '.' means no element name.
        if (token[0] == '*')
        {
            i = 1;
        }
        else if (char.IsLetter(token[0]))
        {
            var start = i;
            while (i < token.Length && (char.IsLetterOrDigit(token[i]) || token[i] == '-' || token[i] == '_'))
                i++;
            element = token[start..i].ToLowerInvariant();
        }
        else if (token[0] is '[' or ':' or '.')
        {
            // Universal selector with class/attribute/pseudo parts only; element stays null.
        }
        else
        {
            return null; // Unsupported leading character.
        }

        // Class, id, attribute and pseudo parts.
        while (i < token.Length)
        {
            var c = token[i];
            if (c == '.')
            {
                var start = ++i;
                while (i < token.Length && (char.IsLetterOrDigit(token[i]) || token[i] == '-' || token[i] == '_'))
                    i++;
                if (start >= i) return null;
                classes.Add(token[start..i]);
            }
            else if (c == '#')
            {
                var start = ++i;
                while (i < token.Length && (char.IsLetterOrDigit(token[i]) || token[i] == '-' || token[i] == '_'))
                    i++;
                if (start >= i) return null;
                ids.Add(token[start..i]);
            }
            else if (c == '[')
            {
                var close = token.IndexOf(']', i);
                if (close < 0) return null;
                var attr = ParseAttribute(token[(i + 1)..close]);
                if (attr is null) return null;
                attributes.Add(attr);
                i = close + 1;
            }
            else if (c == ':')
            {
                var start = ++i;
                while (i < token.Length && token[i] is not '(' and not '[')
                    i++;
                var name = token[start..i].ToLowerInvariant();
                if (name == "not" && i < token.Length && token[i] == '(')
                {
                    var close = FindMatchingParen(token, i);
                    if (close < 0) return null;
                    foreach (var part in SplitTopLevel(token[(i + 1)..close], ','))
                    {
                        var negated = ParseCompound(part.Trim());
                        if (negated is null) return null;
                        nots.Add(negated);
                    }
                    i = close + 1;
                }
                else
                {
                    return null; // Unsupported pseudo-class.
                }
            }
            else
            {
                return null; // Unsupported syntax.
            }
        }

        return new MiniCompound { Element = element, Classes = classes, Ids = ids, Attributes = attributes, Not = nots };
    }

    /// <summary>Parses an attribute selector body: <c>name</c>, <c>name=value</c>, <c>name~=value</c>, ...</summary>
    private static MiniAttribute? ParseAttribute(string text)
    {
        text = text.Trim();
        if (text.Length == 0)
            return null;

        var opStart = -1;
        var opLen = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '=') { opStart = i; opLen = 1; break; }
            if (text[i] is '~' or '|' or '^' or '$' or '*' && i + 1 < text.Length && text[i + 1] == '=')
            {
                opStart = i; opLen = 2; break;
            }
        }

        if (opStart < 0)
            return new MiniAttribute { Name = text.ToLowerInvariant() };

        var name = text[..opStart].Trim().ToLowerInvariant();
        if (name.Length == 0)
            return null;
        var op = text[opStart..(opStart + opLen)];
        var value = text[(opStart + opLen)..].Trim();
        if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
            value = value[1..^1];
        return new MiniAttribute { Name = name, Operator = op, Value = value };
    }

    /// <summary>Index of the <c>)</c> matching the <c>(</c> at <paramref name="openIndex"/>, or -1.</summary>
    private static int FindMatchingParen(string text, int openIndex)
    {
        var depth = 0;
        for (var i = openIndex; i < text.Length; i++)
        {
            if (text[i] == '(') depth++;
            else if (text[i] == ')')
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return -1;
    }

    /// <summary>Splits on a delimiter at the top level (not inside parentheses).</summary>
    private static IEnumerable<string> SplitTopLevel(string text, char delimiter)
    {
        var depth = 0;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '(') depth++;
            else if (c == ')') depth = Math.Max(0, depth - 1);
            else if (c == delimiter && depth == 0)
            {
                yield return text[start..i];
                start = i + 1;
            }
        }
        yield return text[start..];
    }

    /// <summary>Finds the index of the '}' matching the '{' at <paramref name="open"/>.</summary>
    private static int FindMatchingBrace(string text, int open, int end)
    {
        var depth = 0;
        for (var i = open; i < end; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}')
            {
                depth--;
                if (depth == 0)
                    return i;
            }
        }
        return -1;
    }
}
