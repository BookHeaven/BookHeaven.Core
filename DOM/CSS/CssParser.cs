using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Css;
using AngleSharp.Css.Dom;
using AngleSharp.Dom;
using AsCssParser = AngleSharp.Css.Parser.CssParser;

namespace BookHeaven.Core.DOM.CSS;

/// <summary>
/// Computes an element's used (computed) style, replacing AngleSharp.Css's
/// <c>ComputeStyle</c> pipeline.
///
/// AngleSharp.Css 1.x parses declaration values into internal node trees at parse time
/// and cannot round-trip every CSS value: it has no converter for <c>max()</c>,
/// <c>min()</c> and <c>clamp()</c>, and its <c>calc()</c> parser does not accept
/// <c>var()</c> operands. A declaration containing one of these is destroyed when the
/// sheet is parsed — a shorthand is dropped as a whole, so even its plain parts are
/// lost and the cascade silently falls back to the UA or initial value. By the time the
/// cascade is read, the author's text is gone from AngleSharp's model.
///
/// The strategy: ask AngleSharp only for what it is good at — the cascade
/// (UA + author + inheritance from the parent declaration, shorthand expansion) — and do
/// the rest ourselves:
/// <list type="number">
/// <item>values the author wrote with a function are recovered verbatim from the raw
/// text of the document's <c>&lt;style&gt;</c> elements and the element's own
/// <c>style</c> attribute (selectors re-matched with AngleSharp's selector engine,
/// specificity, source order and <c>!important</c> honoured), and override the
/// destroyed cascade values — but never a plain value the cascade already carries
/// from a declaration that wins the cascade;</item>
/// <item>custom properties (<c>--*</c>) are resolved iteratively against the inherited
/// scope (parent first, own declarations override), honouring <c>var()</c> fallbacks;</item>
/// <item><c>inherit</c>/<c>unset</c>/<c>initial</c> keywords the cascade leaves raw are
/// resolved or removed;</item>
/// <item>values containing <c>var()</c>/<c>calc()</c>/<c>max()</c>/<c>min()</c>/<c>clamp()</c>
/// or em/rem units are evaluated to px through <see cref="CSS.CssLengthParser"/>, using
/// browser-correct references (em against the parent's own font size, % against the
/// page width — or the page height for the height properties — per property).</item>
/// </list>
/// Values that still cannot be resolved are left raw; the engines' length parser (which
/// also understands these functions) is the safety net and degrades them to defaults.
/// Rules inside <c>@media</c> or other at-rules are not indexed for recovery (their
/// condition is not evaluated here), so function values there keep the cascade-fallback
/// behaviour.
/// </summary>
public static class CssParser
{
    // Values that need our compute step: any CSS value function, or em/rem units
    // (plain %, px and friends are left raw — the engines parse them with their own
    // per-property reference, exactly as before this class existed).
    // The unitless-number alternative's lookahead must reject digits, '.', '%' and
    // letters so a bare % value like "100%" (or "10.5%") never matches a digit
    // prefix: if it did, ComputeStyle would pre-resolve the % against the viewport
    // instead of leaving it raw for the engine to resolve against the element's
    // containing block (which is only the page at the top level).
    private static readonly Regex NeedsResolutionRegex = new(
        @"(?:var|calc|max|min|clamp)\s*\(|\d+(?:\.\d+)?(?:rem|em)\b|\d+(?:\.\d+)?(?![\d%a-zA-Z.])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Value functions AngleSharp.Css's parsers cannot survive (see class remarks): a
    // declaration containing one is destroyed at parse time and needs raw-text recovery.
    private static readonly Regex HasUnparseableFunctionRegex = new(
        @"(?:var|calc|max|min|clamp)\s*\(",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ImportantSuffixRegex = new(
        @"\s+!\s*important\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // A unit token inside an expression: a digit (optionally with a fractional part)
    // directly followed by unit letters or %. Function names never match — their
    // letters are not preceded by a digit ("calc(1.55 * 1)" is unitless,
    // "calc(1.55em * 1)" and "max(10px, 2)" are not).
    private static readonly Regex HasUnitTokenRegex = new(
        @"\d+(?:\.\d+)?[a-zA-Z%]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Properties that inherit per the CSS spec and that the layout engines read.
    // The cascade already inherits standard properties from the parent declaration,
    // but this list drives the explicit "inherit"/"unset" keyword resolution.
    private static readonly HashSet<string> InheritableProperties =
    [
        "font-size", "line-height", "font-weight", "font-style", "font-family", "font-stretch",
        "font-variant", "font-variant-east-asian", "color", "text-align", "text-indent",
        "letter-spacing", "word-spacing", "white-space", "direction"
    ];

    /// <summary>
    /// Computes the used style of <paramref name="element"/>. Never throws.
    /// </summary>
    /// <param name="element">The element to compute.</param>
    /// <param name="renderDevice">
    /// Render device handed to the cascade (page size for viewport units).
    /// </param>
    /// <param name="parentComputed">
    /// The already-computed style of the parent element. AngleSharp inherits standard
    /// inheritable properties and custom properties from this declaration; passing the
    /// parent's *computed* style (instead of its cascade) is what makes em/% resolution
    /// use the browser-correct reference values. May be null for the root element.
    /// </param>
    /// <returns>The computed style declaration, or null when the element has no document/window.</returns>
    public static ICssStyleDeclaration? ComputeStyle(
        IElement element,
        IRenderDevice renderDevice,
        ICssStyleDeclaration? parentComputed = null)
    {
        if (element.Owner is not { DefaultView: { } window } doc)
            return null;

        ICssStyleDeclaration cascaded;
        try
        {
            var styles = window.GetStyleCollection(renderDevice);
            // AngleSharp requires a non-null parent declaration; a detached element's
            // empty style declaration is a safe placeholder for the root of the chain.
            var parentForCascade = parentComputed ?? doc.CreateElement("span").GetStyle();
            cascaded = styles.ComputeCascadedStyle(element, parentForCascade);
        }
        catch
        {
            return null;
        }

        var vars = BuildCustomPropertyTable(cascaded, parentComputed);
        WriteCustomProperties(cascaded, vars);

        // AngleSharp.Css destroys var/calc/max/min/clamp values at parse time (see class
        // remarks): recover the author's raw text for the declarations that apply to this
        // element and override the destroyed cascade values with it.
        var rawOverrides = GetRawOverrides(doc, element);

        var parentFontSize = ParseFontSize(parentComputed) ?? renderDevice.FontSize;
        var ownFontSize = ParseFontSize(
            cascaded, (float)parentFontSize, vars, renderDevice,
            rawOverrides.TryGetValue("font-size", out var fontSizeOverride) ? fontSizeOverride.Value : null);

        // Collect the decisions first and apply them afterwards: mutating the
        // declaration while enumerating it is not safe.
        var updates = new List<(string Name, string? Value, bool Important)>();
        foreach (var property in cascaded)
        {
            if (property.Name.StartsWith("--", StringComparison.Ordinal))
                continue; // custom properties were resolved above

            var name = property.Name;
            var hasRawOverride = rawOverrides.TryGetValue(name, out var rawOverride);
            var value = hasRawOverride ? rawOverride.Value : property.Value;
            var updated = ResolveKeywords(name, value, parentComputed);

            if (updated is not null)
            {
                updated = ResolveVarReferences(updated, vars);
                if (NeedsResolutionRegex.IsMatch(updated))
                    updated = EvaluateToPx(updated, name, ownFontSize ?? (float)parentFontSize, (float)parentFontSize, doc, element, renderDevice)
                              ?? updated; // unresolvable: keep the var-substituted raw value
            }

            if (updated is null)
                updates.Add((name, null, false));
            else if (hasRawOverride || updated != value)
                // A raw override always applies, even when it needs no evaluation: the
                // cascade value it replaces is destroyed (or missing), so "unchanged"
                // does not mean "already correct".
                updates.Add((name, updated, hasRawOverride ? rawOverride.Important : property.IsImportant));
        }

        foreach (var (name, value, important) in updates)
        {
            if (value is null) cascaded.RemoveProperty(name);
            else cascaded.SetProperty(name, value, important ? "important" : null);
        }

        return cascaded;
    }

    /// <summary>
    /// Computes the style of an element and all its ancestors (bottom-up), without
    /// caching. Convenience for callers outside the engines (tests, diagnostics).
    /// </summary>
    public static ICssStyleDeclaration? ComputeStyleChain(IElement element, IRenderDevice renderDevice)
    {
        var ancestors = new List<IElement>();
        for (var parent = element.ParentElement; parent is not null; parent = parent.ParentElement)
            ancestors.Add(parent);
        ancestors.Reverse();

        ICssStyleDeclaration? computed = null;
        foreach (var ancestor in ancestors)
            computed = ComputeStyle(ancestor, renderDevice, computed) ?? computed;

        return ComputeStyle(element, renderDevice, computed) ?? computed;
    }

    // ------------------------------------------------------------------
    // Raw author declarations (recovery of values AngleSharp.Css destroyed)
    // ------------------------------------------------------------------

    /// <summary>
    /// A declaration copied verbatim from the author's stylesheet text (style elements
    /// or style attributes). Names are lower-cased; selectors are kept as written.
    /// </summary>
    private sealed record RawAuthorDeclaration(string Selector, string Name, string Value, bool Important)
    {
        /// <summary>True when the value contains a function AngleSharp.Css cannot parse.</summary>
        public bool HasFunction => HasUnparseableFunctionRegex.IsMatch(Value);
    }

    // AngleSharp's own selector parser, used only to obtain the specificity of the
    // selectors we match ourselves (the raw index keeps the selector text as written).
    private static readonly AsCssParser SelectorSpecificityParser = new();

    // Inline style attributes beat any selector; an empty selector text denotes the
    // element's own style attribute in the raw index.
    private static readonly Priority InlineStyleSpecificity = new(255, 255, 255, 255);

    private static readonly ConcurrentDictionary<string, Priority> SelectorSpecificityCache = new(StringComparer.Ordinal);

    /// <summary>
    /// The specificity of <paramref name="selector"/>, parsed with AngleSharp's selector
    /// parser so it matches the one its cascade uses. Unparsable selectors degrade to
    /// zero specificity (they match nothing in the raw index anyway).
    /// </summary>
    private static Priority GetSelectorSpecificity(string selector)
    {
        if (selector.Length == 0)
            return InlineStyleSpecificity;

        if (SelectorSpecificityCache.TryGetValue(selector, out var cached))
            return cached;

        var specificity = default(Priority);
        try
        {
            // A selector list's specificity is the maximum of its parts, which is what
            // parsing the whole list yields.
            var rule = SelectorSpecificityParser
                .ParseStyleSheet(selector + " { z-index: 0 }")
                .Rules.OfType<ICssStyleRule>().FirstOrDefault();
            specificity = rule?.Selector?.Specificity ?? default;
        }
        catch
        {
            // Unreadable selector: zero specificity.
        }

        SelectorSpecificityCache[selector] = specificity;
        return specificity;
    }

    // CSS cascade order: ids, then classes/attributes/pseudo-classes, then tags.
    private static bool BeatsSpecificity(Priority a, Priority b)
        => (a.Ids, a.Classes, a.Tags, a.Inlines)
            .CompareTo((b.Ids, b.Classes, b.Tags, b.Inlines)) > 0;

    /// <summary>
    /// Per-document index of the raw author declarations: the declarations in source
    /// order, plus for every element the indices of the declarations whose selector
    /// matches it. The match sets are precomputed with one <c>QuerySelectorAll</c> pass
    /// per distinct selector instead of re-matching every selector against every
    /// element, which is orders of magnitude cheaper.
    /// </summary>
    private sealed record RawOverridesIndex(List<RawAuthorDeclaration> Declarations, Dictionary<IElement, List<int>> Matched);

    private static readonly ConditionalWeakTable<IDocument, RawOverridesIndex> RawOverridesIndexCache = new();

    /// <summary>
    /// Shorthands expanded to their longhands. Four entries use the box-model value
    /// pattern (1→all, 2→top+left/right+bottom, 3→top+right/left+bottom); two entries
    /// are positional, except <c>width</c>, where one value sets only width.
    /// </summary>
    private static readonly Dictionary<string, string[]> ShorthandLonghands = new(StringComparer.OrdinalIgnoreCase)
    {
        ["margin"] = ["margin-top", "margin-right", "margin-bottom", "margin-left"],
        ["padding"] = ["padding-top", "padding-right", "padding-bottom", "padding-left"],
        ["inset"] = ["top", "right", "bottom", "left"],
        ["margin-block"] = ["margin-block-start", "margin-block-end"],
        ["margin-inline"] = ["margin-inline-start", "margin-inline-end"],
        ["padding-block"] = ["padding-block-start", "padding-block-end"],
        ["padding-inline"] = ["padding-inline-start", "padding-inline-end"],
        ["width"] = ["width", "height"],
    };

    private static RawOverridesIndex GetRawOverridesIndex(IDocument doc)
    {
        return RawOverridesIndexCache.GetOrAdd(doc, d =>
        {
            var declarations = new List<RawAuthorDeclaration>();
            try
            {
                foreach (var style in d.QuerySelectorAll("style"))
                    ParseStyleText(style.TextContent, declarations);
            }
            catch
            {
                // Unreadable stylesheet: nothing to recover.
            }

            var matched = new Dictionary<IElement, List<int>>();
            var selectorMatches = new Dictionary<string, IElement[]>(StringComparer.Ordinal);
            IElement[]? universe = null;

            IElement[] GetMatches(string selector)
            {
                if (selectorMatches.TryGetValue(selector, out var cached))
                    return cached;
                IElement[] matches;
                try
                {
                    matches = d.QuerySelectorAll(selector).ToArray();
                }
                catch
                {
                    matches = []; // selector the engine cannot handle: leave the cascade alone
                }
                selectorMatches[selector] = matches;
                return matches;
            }

            void AddMatch(IElement element, int declarationIndex)
            {
                if (!matched.TryGetValue(element, out var list))
                    matched[element] = list = [];
                list.Add(declarationIndex);
            }

            // Walk the declarations in source order so each element's list keeps source
            // order (the cascade needs it: within a tier, later declarations win).
            for (var i = 0; i < declarations.Count; i++)
            {
                var selector = declarations[i].Selector;
                if (selector.Length == 0)
                {
                    // An empty selector matches every element of the document.
                    if (universe is null)
                    {
                        try { universe = d.QuerySelectorAll("*").ToArray(); }
                        catch { universe = []; }
                    }
                    foreach (var element in universe)
                        AddMatch(element, i);
                    continue;
                }

                foreach (var element in GetMatches(selector))
                    AddMatch(element, i);
            }

            return new RawOverridesIndex(declarations, matched);
        });
    }

    /// <summary>
    /// Scans raw stylesheet text for top-level <c>selector { declarations }</c> rules
    /// and records every declaration verbatim. Only unconditional rules are indexed:
    /// at-rules (<c>@media</c>, <c>@supports</c>, ...) are skipped because their
    /// condition is not evaluated here.
    /// </summary>
    private static void ParseStyleText(string css, List<RawAuthorDeclaration> result)
    {
        css = StripCssComments(css);
        var i = 0;
        while (i < css.Length)
        {
            var open = css.IndexOf('{', i);
            if (open < 0) return;
            var selector = css[i..open].Trim();
            var close = FindMatchingBrace(css, open);
            if (close < 0) return;

            if (!selector.StartsWith("@", StringComparison.Ordinal))
                ParseStyleDeclarations(css[(open + 1)..close], selector, result);

            i = close + 1;
        }
    }

    private static int FindMatchingBrace(string css, int openIndex)
    {
        var depth = 0;
        for (var i = openIndex; i < css.Length; i++)
        {
            if (css[i] == '{') depth++;
            else if (css[i] == '}' && --depth == 0) return i;
        }
        return -1;
    }

    private static string StripCssComments(string css)
    {
        var start = css.IndexOf("/*", StringComparison.Ordinal);
        if (start < 0) return css;

        var sb = new StringBuilder(css.Length);
        var i = 0;
        while (start >= 0)
        {
            sb.Append(css, i, start - i);
            var end = css.IndexOf("*/", start + 2, StringComparison.Ordinal);
            if (end < 0) { i = css.Length; break; } // unterminated comment: drop the rest
            i = end + 2;
            start = css.IndexOf("/*", i, StringComparison.Ordinal);
        }
        sb.Append(css, i, css.Length - i);
        return sb.ToString();
    }

    /// <summary>
    /// Splits a declaration block (or a <c>style</c> attribute) on top-level
    /// <c>;</c> and records each <c>name: value</c> verbatim, with
    /// <c>!important</c> detected.
    /// </summary>
    private static void ParseStyleDeclarations(string block, string selector, List<RawAuthorDeclaration> result)
    {
        foreach (var part in CssLengthParser.SplitTopLevel(block, c => c == ';'))
        {
            var declaration = part.Trim();
            if (declaration.Length == 0) continue;

            var colon = declaration.IndexOf(':');
            if (colon <= 0) continue;
            var name = declaration[..colon].Trim();
            if (name.Length == 0 || name.IndexOfAny([' ', '\t', '\r', '\n']) >= 0) continue;

            var value = declaration[(colon + 1)..].Trim();
            if (value.Length == 0) continue;

            var important = false;
            if (ImportantSuffixRegex.IsMatch(value))
            {
                important = true;
                value = ImportantSuffixRegex.Replace(value, string.Empty).Trim();
                if (value.Length == 0) continue;
            }

            result.Add(new RawAuthorDeclaration(selector, name.ToLowerInvariant(), value, important));
        }
    }

    /// <summary>
    /// The author's raw declarations that apply to <paramref name="element"/> and were
    /// destroyed by AngleSharp.Css at parse time, reduced to one winner per longhand
    /// using the CSS cascade: !important declarations beat normal ones, and within a
    /// tier the higher specificity wins, ties broken by later source order (the
    /// element's style attribute is last and outranks every selector).
    ///
    /// A longhand is overridden only when its winning declaration is one AngleSharp
    /// could not parse (its value contains a function): a plain value parsed
    /// losslessly, so the cascade already carries it and must not be clobbered — in
    /// particular a plain value from a more specific selector must survive a function
    /// value from a less specific one. When the winner comes from a shorthand, every
    /// longhand the shorthand sets is overridden — even the plain parts — because
    /// AngleSharp drops the whole shorthand when any part fails.
    /// </summary>
    private static Dictionary<string, (string Value, bool Important)> GetRawOverrides(IDocument doc, IElement element)
    {
        var result = new Dictionary<string, (string Value, bool Important)>(StringComparer.Ordinal);

        var index = GetRawOverridesIndex(doc);
        var matching = new List<RawAuthorDeclaration>();
        if (index.Matched.TryGetValue(element, out var matched))
            foreach (var declarationIndex in matched)
                matching.Add(index.Declarations[declarationIndex]);

        // The element's own style attribute is the strongest author origin: last.
        var inline = element.GetAttribute("style");
        if (!string.IsNullOrWhiteSpace(inline))
            ParseStyleDeclarations(inline, string.Empty, matching);

        if (matching.Count == 0) return result;

        foreach (var importantTier in new[] { false, true })
        {
            var winners = new Dictionary<string, (string Value, bool HasFunction, Priority Specificity, int Order)>(StringComparer.Ordinal);
            for (var i = 0; i < matching.Count; i++)
            {
                var declaration = matching[i];
                if (declaration.Important != importantTier) continue;
                var targets = ExpandShorthand(declaration.Name, declaration.Value)
                               ?? [(declaration.Name, declaration.Value)];
                var specificity = GetSelectorSpecificity(declaration.Selector);
                foreach (var (longhand, partValue) in targets)
                {
                    var candidate = (Value: partValue, HasFunction: declaration.HasFunction, Specificity: specificity, Order: i);
                    if (!winners.TryGetValue(longhand, out var current)
                        || (BeatsSpecificity(candidate.Specificity, current.Specificity)
                            || (candidate.Specificity == current.Specificity && candidate.Order > current.Order)))
                        winners[longhand] = candidate;
                }
            }

            foreach (var (longhand, (value, hasFunction, _, _)) in winners)
                if (hasFunction)
                    result[longhand] = (value, importantTier);
        }

        return result;
    }

    /// <summary>
    /// Expands a shorthand value into its longhand parts. Returns null for names that
    /// are not known shorthands or for an invalid part count.
    /// </summary>
    private static List<(string Name, string Value)>? ExpandShorthand(string name, string value)
    {
        if (!ShorthandLonghands.TryGetValue(name, out var longhands)) return null;
        var parts = CssLengthParser.SplitTopLevel(value, char.IsWhiteSpace);
        if (parts.Count == 0) return null;

        if (longhands.Length == 4)
        {
            if (parts.Count > 4) return null;
            var top = parts[0];
            var right = parts.Count >= 2 ? parts[1] : top;
            var bottom = parts.Count >= 3 ? parts[2] : top;
            var left = parts.Count >= 4 ? parts[3] : right;
            return [(longhands[0], top), (longhands[1], right), (longhands[2], bottom), (longhands[3], left)];
        }

        if (longhands.Length == 2)
        {
            if (parts.Count > 2) return null;
            if (name.Equals("width", StringComparison.OrdinalIgnoreCase))
                return parts.Count == 1
                    ? [(longhands[0], parts[0])]
                    : [(longhands[0], parts[0]), (longhands[1], parts[1])];
            // *-block / *-inline: one value sets both longhands.
            return parts.Count == 1
                ? [(longhands[0], parts[0]), (longhands[1], parts[0])]
                : [(longhands[0], parts[0]), (longhands[1], parts[1])];
        }

        return null;
    }

    // ------------------------------------------------------------------
    // Custom properties
    // ------------------------------------------------------------------

    /// <summary>
    /// Merged <c>--*</c> table: the parent's (already resolved against its own scope)
    /// first, then the element's own cascade declarations (own wins), each resolved
    /// against the merged table so <c>--a: var(--b)</c> chains work.
    /// </summary>
    private static Dictionary<string, string> BuildCustomPropertyTable(ICssStyleDeclaration cascaded, ICssStyleDeclaration? parentComputed)
    {
        var vars = new Dictionary<string, string>(StringComparer.Ordinal);
        if (parentComputed is not null)
            foreach (var property in parentComputed)
                if (property.Name.StartsWith("--", StringComparison.Ordinal))
                    vars[property.Name] = property.Value;
        foreach (var property in cascaded)
            if (property.Name.StartsWith("--", StringComparison.Ordinal))
                vars[property.Name] = ResolveVarReferences(property.Value, vars);
        return vars;
    }

    private static void WriteCustomProperties(ICssStyleDeclaration cascaded, IReadOnlyDictionary<string, string> vars)
    {
        var updates = new List<(string Name, string Value, bool Important)>();
        foreach (var property in cascaded)
        {
            if (!property.Name.StartsWith("--", StringComparison.Ordinal)) continue;
            if (vars.TryGetValue(property.Name, out var resolved) && resolved != property.Value)
                updates.Add((property.Name, resolved, property.IsImportant));
        }
        foreach (var (name, value, important) in updates)
            cascaded.SetProperty(name, value, important ? "important" : null);
    }

    /// <summary>
    /// Substitutes every <c>var(--name, fallback)</c> in <paramref name="value"/> against
    /// <paramref name="vars"/>. Unresolved names without a fallback are left raw (invalid
    /// at computed-value time per spec; the raw value degrades downstream). Repeats until
    /// fixpoint (bounded) so a var whose replacement contains vars resolves too.
    /// </summary>
    private static string ResolveVarReferences(string value, IReadOnlyDictionary<string, string> vars)
    {
        if (!value.Contains("var(", StringComparison.OrdinalIgnoreCase)) return value;

        var current = value;
        for (int pass = 0; pass < 10 && current.Contains("var(", StringComparison.OrdinalIgnoreCase); pass++)
        {
            var sb = new StringBuilder(current.Length);
            var changed = false;
            var i = 0;
            while (i < current.Length)
            {
                if (!current.AsSpan(i).StartsWith("var(", StringComparison.OrdinalIgnoreCase))
                {
                    sb.Append(current[i++]);
                    continue;
                }

                int depth = 0;
                var end = -1;
                for (int j = i; j < current.Length; j++)
                {
                    if (current[j] == '(') depth++;
                    else if (current[j] == ')' && --depth == 0) { end = j; break; }
                }
                if (end < 0)
                {
                    sb.Append(current[i++]); // unbalanced; keep raw
                    continue;
                }

                var inner = current[(i + 4)..end].Trim();
                var name = inner;
                string? fallback = null;
                int parenDepth = 0;
                for (int j = 0; j < inner.Length; j++)
                {
                    if (inner[j] == '(') parenDepth++;
                    else if (inner[j] == ')') parenDepth--;
                    else if (inner[j] == ',' && parenDepth == 0)
                    {
                        name = inner[..j].Trim();
                        fallback = inner[(j + 1)..].Trim();
                        break;
                    }
                }

                if (vars.TryGetValue(name, out var replacement))
                {
                    if (replacement != current[i..(end + 1)]) changed = true;
                    sb.Append(replacement);
                }
                else if (fallback is not null)
                {
                    if (fallback != current[i..(end + 1)]) changed = true;
                    sb.Append(fallback);
                }
                else
                    sb.Append(current[i..(end + 1)]); // unresolved, no fallback: keep raw

                i = end + 1;
            }

            if (!changed) return current;
            current = sb.ToString();
        }
        return current;
    }

    // ------------------------------------------------------------------
    // Keywords and value evaluation
    // ------------------------------------------------------------------

    /// <summary>
    /// Resolves the <c>inherit</c>/<c>unset</c>/<c>initial</c> keywords that the cascade
    /// leaves raw. Returns the replacement value, or null when the property must be removed.
    /// </summary>
    private static string? ResolveKeywords(string name, string value, ICssStyleDeclaration? parentComputed)
    {
        switch (value.ToLowerInvariant())
        {
            case "initial":
                return null;

            case "inherit":
            case "unset" when InheritableProperties.Contains(name):
                return parentComputed?.GetPropertyValue(name);

            case "unset":
                return null;

            default:
                return value;
        }
    }

    /// <summary>
    /// Evaluates a var-resolved value to px through <see cref="CSS.CssLengthParser"/> using
    /// browser-correct references, and returns it as an absolute <c>px</c> value.
    /// Returns null when the value cannot be evaluated.
    /// </summary>
    private static string? EvaluateToPx(
        string value,
        string name,
        float ownFontSize,
        float parentFontSize,
        IDocument doc,
        IElement element,
        IRenderDevice? renderDevice)
    {
        var emReference = name.Equals("font-size", StringComparison.OrdinalIgnoreCase) ? parentFontSize : ownFontSize;
        var reference = name.Equals("line-height", StringComparison.OrdinalIgnoreCase)
            ? ownFontSize
            : PercentReference(name, parentFontSize, renderDevice);

        var px = CssLengthParser.ParseLengthToPx(value, renderDevice, reference, emReference, doc, element);
        if (px is null) return null;
        // A value whose (var-substituted) expression carries no unit token is a
        // unitless number, not a length: per spec its computed value is the number
        // itself (e.g. "calc(var(--line-height) * 1)" -> "1.55"), which the engines
        // interpret as a multiplier of the element's own font size.
        return HasUnitTokenRegex.IsMatch(value) ? FormatPx(px.Value) : px.Value.ToString("0.##", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The reference a <c>%</c> inside a value function resolves against, per property.
    /// Per the CSS spec, % margins and % paddings resolve against the containing
    /// block's WIDTH on every side — only the height properties use the height. Here
    /// the page is the containing block, matching the layout engines' convention.
    /// </summary>
    private static float PercentReference(string name, float parentFontSize, IRenderDevice? renderDevice)
    {
        return name.ToLowerInvariant() switch
        {
            "font-size" => parentFontSize,
            "width" or "min-width" or "max-width"
                or "margin-top" or "margin-right" or "margin-bottom" or "margin-left"
                or "margin-block" or "margin-block-start" or "margin-block-end"
                or "margin-inline" or "margin-inline-start" or "margin-inline-end"
                or "padding-top" or "padding-right" or "padding-bottom" or "padding-left"
                or "padding-block" or "padding-block-start" or "padding-block-end"
                or "padding-inline" or "padding-inline-start" or "padding-inline-end"
                or "text-indent"
                => renderDevice?.ViewPortWidth ?? 0,
            "height" or "min-height" or "max-height"
                => renderDevice?.ViewPortHeight ?? 0,
            _ => 1f,
        };
    }

    /// <summary>
    /// Parses the font-size of a declaration to px. <c>em</c> in a font-size resolves
    /// against the PARENT's font size (browser rule), so the parent reference is used.
    /// </summary>
    private static float? ParseFontSize(ICssStyleDeclaration? declaration, float? parentFontSize = null, Dictionary<string, string>? vars = null, IRenderDevice? renderDevice = null, string? rawValue = null)
    {
        if (declaration is null) return null;
        var raw = rawValue ?? declaration.GetPropertyValue("font-size");
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var value = raw;
        if (vars is not null && renderDevice is not null)
        {
            value = ResolveVarReferences(value, vars);
            if (NeedsResolutionRegex.IsMatch(value))
            {
                var reference = parentFontSize ?? (float)(renderDevice.FontSize);
                var px = CssLengthParser.ParseLengthToPx(value, renderDevice, reference, reference);
                if (px is not null) return px;
            }
        }

        // A raw % font-size must resolve against the parent's font size (browser rule),
        // so the reference and the em reference coincide here.
        var fontSizeReference = parentFontSize ?? (float)(renderDevice?.FontSize ?? 16);
        return CssLengthParser.ParseLengthToPx(value, renderDevice ?? new DefaultRenderDevice(),
            reference: fontSizeReference, emReference: fontSizeReference);
    }

    private static string FormatPx(float px)
    {
        if (px is > -0.005f and < 0.005f) px = 0f;
        return $"{px.ToString("0.##", CultureInfo.InvariantCulture)}px";
    }
}
