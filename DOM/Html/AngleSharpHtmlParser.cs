using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Dom;
using BookHeaven.Core.DOM.Services;

namespace BookHeaven.Core.DOM.Html;

/// <summary>
/// Thin wrapper around AngleSharp to parse HTML and extract style-related artifacts.
/// This class keeps the integration surface small so later the full style resolver can reuse it.
/// </summary>
public sealed class AngleSharpHtmlParser
{
    private readonly IBrowsingContext _context;
    private readonly PageCalculatorOptions _options;

    /// <summary>
    /// The render device registered with AngleSharp. Always has positive
    /// dimensions and font size, otherwise em/rem/% length computation throws.
    /// </summary>
    public IRenderDevice RenderDevice { get; }

    public AngleSharpHtmlParser() : this(null) { }

    public AngleSharpHtmlParser(PageCalculatorOptions? options)
    {
        // AngleSharp throws ArgumentException (and aborts the whole element's
        // computed style) when a length uses em/rem with a zero font size or
        // %/vw/vh with zero viewport dimensions. Note `options?.X ?? default`
        // does not protect against a non-null options object whose fields are
        // zero, hence the explicit clamping.
        RenderDevice = new DefaultRenderDevice
        {
            DeviceWidth = ClampPositive(options?.PageWidthPx, PageCalculatorOptions.DefaultPageWidthPx),
            DeviceHeight = ClampPositive(options?.PageHeightPx, PageCalculatorOptions.DefaultPageHeightPx),
            ViewPortWidth = ClampPositive(options?.PageWidthPx, PageCalculatorOptions.DefaultPageWidthPx),
            ViewPortHeight = ClampPositive(options?.PageHeightPx, PageCalculatorOptions.DefaultPageHeightPx),
            FontSize = ClampPositive(options?.RootFontSizePx, PageCalculatorOptions.DefaultRootFontSizePx)
        };

        // Register the render device first, then enable CSS so the CSS services see the device
        var config = Configuration.Default.WithCss().WithRenderDevice(RenderDevice);


        _options = options ?? new PageCalculatorOptions();
        _context = BrowsingContext.New(config);
    }

    // Logical-margin expansion results per source CSS string. A parser is
    // reused across chapters that share the same stylesheet, so expansion
    // (regex scanning + override generation) only happens once per unique CSS.
    private readonly Dictionary<string, string> _expandedCssCache = [];

    private static readonly Regex MarginBlockStartRegex = new(@"(?<![\w-])margin-block-start\s*:\s*([^;}]+);?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MarginBlockEndRegex = new(@"(?<![\w-])margin-block-end\s*:\s*([^;}]+);?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MarginBlockRegex = new(@"(?<![\w-])margin-block\s*:\s*([^;}]+);?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MarginShorthandRegex = new(@"(?<![\w-])margin\s*:\s*([^;}]+);?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RuleRegex = new(@"(?<selector>[^\{]+)\{(?<body>[^\}]*)\}", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex MarginTopRegex = new(@"(?<![\w-])margin-top\s*:\s*([^;}]+);?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MarginBottomRegex = new(@"(?<![\w-])margin-bottom\s*:\s*([^;}]+);?", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static int ClampPositive(int? value, int fallback) => value is > 0 ? value.Value : fallback;

    private static float ClampPositive(float? value, float fallback) => value is > 0 ? value.Value : fallback;

    /// <summary>
    /// Parse HTML and optional CSS (injected into a &lt;style&gt; tag) and return the AngleSharp document.
    /// </summary>
    public async Task<IDocument> ParseDocumentAsync(string htmlFragment, IReadOnlyList<string>? css = null)
    {
        var defaultCss = ExpandedCssCacheGetOrAdd(BuildGlobalCss(_options));
        
        // Expand logical margins (margin-block/shorthand -> physical longhands)
        // before AngleSharp sees the sheet: AngleSharp does not map logical
        // margin properties onto margin-top/bottom, so without this
        // preprocessing those rules apply to nothing.
        var injectedCss = string.Empty;
        if (css?.Count > 0)
        {
            var cssBuilder = new StringBuilder();
            foreach (var styles in css ?? [])
            {
                cssBuilder.AppendLine("<style>");
                cssBuilder.AppendLine(ExpandedCssCacheGetOrAdd(styles));
                cssBuilder.AppendLine("</style>");
            }
            injectedCss = cssBuilder.ToString();
        }
        
        var html = $"""
                   <html>
                       <head>
                            <style>
                                {defaultCss}
                            </style>
                            {injectedCss}
                       </head>
                       <body>
                            {htmlFragment}
                       </body>
                   </html>
                   """;

        var doc = await _context.OpenAsync(req => req.Content(html));
        //PruneUnmatchedRules(doc);
        return doc;
    }

    /// <summary>
    /// Replaces each &lt;style&gt; element's text with a copy that drops rules whose
    /// selectors match no element of the document. AngleSharp's cascade re-tests every
    /// rule of the document against every element whose computed style is requested, so
    /// dead rules (TOC, image, RTL and similar selectors are common in EPUBs whose
    /// chapters use only a fraction of the sheet) cost matching time without ever
    /// contributing a declaration; a rule that matches nothing cannot affect any
    /// element's cascade, so the pruning is semantically a no-op.
    /// The CSS object model of AngleSharp.Css exposes no way to remove rules from a
    /// parsed sheet, but changing the &lt;style&gt; element's text forces the sheet to
    /// re-parse, so the pruning happens at the text level: every surviving rule keeps
    /// its original text verbatim (which also keeps the raw var()/calc() values the
    /// raw-overrides index recovers from the &lt;style&gt; text). Pseudo-element
    /// selectors are kept: QuerySelectorAll cannot express them, and unparsable
    /// selectors are kept as well (the cascade ignores them too).
    /// </summary>
    private static void PruneUnmatchedRules(IDocument doc)
    {
        try
        {
            foreach (var styleEl in doc.QuerySelectorAll("style"))
            {
                var text = styleEl.TextContent;
                if (string.IsNullOrWhiteSpace(text)) continue;
                var pruned = PruneCssText(doc, text);
                if (pruned.Length >= text.Length) continue;
                while (styleEl.FirstChild is { } child) ((IChildNode)child).Remove();
                styleEl.AppendChild(doc.CreateTextNode(pruned));
            }
        }
        catch
        {
            // Pruning is a pure optimization; never break the parse over it.
        }
    }

    /// <summary>
    /// Brace-aware single pass over a stylesheet that removes top-level rules (and the
    /// inner rules of grouping at-rules such as @media/@supports) whose selectors match
    /// nothing in <paramref name="doc"/>. Surviving rules and non-group at-rules
    /// (@font-face, @keyframes, ...) are copied verbatim from the original text.
    /// </summary>
    private static string PruneCssText(IDocument doc, string css)
    {
        var sb = new StringBuilder(css.Length);
        var n = css.Length;
        var i = 0;
        while (i < n)
        {
            var c = css[i];
            if (c == '/' && i + 1 < n && css[i + 1] == '*')
            {
                var end = css.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? n : end + 2;
                continue;
            }

            if (c == '@')
            {
                var j = ScanToBlockOrStatement(css, i);
                if (j >= n)
                {
                    sb.Append(css, i, n - i);
                    break;
                }
                if (css[j] == ';')
                {
                    // Statement at-rule (@import ...;): keep whole.
                    sb.Append(css, i, j + 1 - i);
                    i = j + 1;
                    continue;
                }
                if (css[j] != '{')
                {
                    sb.Append(css, i, n - i);
                    break;
                }
                var prelude = css[i..j].Trim().ToLowerInvariant();
                var bodyEnd = FindMatchingBrace(css, j);
                if (bodyEnd < 0)
                {
                    sb.Append(css, i, n - i);
                    break;
                }
                if (!GroupingAtRuleNames.Contains(prelude[1..].Split(' ', 2)[0]))
                {
                    // @font-face, @keyframes, ...: keep whole.
                    sb.Append(css, i, bodyEnd + 1 - i);
                    i = bodyEnd + 1;
                    continue;
                }
                // Grouping at-rule: keep the block, prune its inner rules.
                sb.Append(css, i, j + 1 - i)
                   .Append(PruneCssText(doc, css.AsSpan(j + 1, bodyEnd - j - 1).ToString()))
                   .Append('}');
                i = bodyEnd + 1;
                continue;
            }

            if (c is ';' or '}')
            {
                i++;
                continue;
            }

            // Normal rule: selector up to '{'.
            var k = ScanToBlockOrStatement(css, i);
            if (k >= n || css[k] != '{')
            {
                // Malformed tail (or a stray '@' inside a selector): keep verbatim.
                sb.Append(css, i, n - i);
                break;
            }
            var selector = css.AsSpan(i, k - i).Trim().ToString();
            var bodyEnd2 = FindMatchingBrace(css, k);
            if (bodyEnd2 < 0)
            {
                sb.Append(css, i, n - i);
                break;
            }
            if (SelectorMatchesSomething(doc, selector))
                sb.Append(css, i, bodyEnd2 + 1 - i);
            i = bodyEnd2 + 1;
        }
        return sb.ToString();
    }

    /// <summary>
    /// From position <paramref name="start"/> (first char of a selector or at-rule
    /// prelude), returns the index of the '{' that opens the block, or of the ';'
    /// that ends a statement at-rule. String literals are skipped.
    /// </summary>
    private static int ScanToBlockOrStatement(string css, int start)
    {
        var n = css.Length;
        var inString = false;
        var quote = '\0';
        for (var j = start; j < n; j++)
        {
            var c = css[j];
            if (inString)
            {
                if (c == '\\') j++;
                else if (c == quote) inString = false;
            }
            else if (c is '"' or '\'') { inString = true; quote = c; }
            else if (c is '{' or '}' or ';') return c == '}' ? j + 1 : j;
        }
        return n;
    }

    /// <summary>
    /// Given the index of an opening '{', returns the index of the matching '}',
    /// skipping string literals and comments. Returns -1 when unbalanced.
    /// </summary>
    private static int FindMatchingBrace(string css, int openIdx)
    {
        var n = css.Length;
        var depth = 0;
        var inString = false;
        var quote = '\0';
        for (var j = openIdx; j < n; j++)
        {
            var c = css[j];
            if (inString)
            {
                if (c == '\\') j++;
                else if (c == quote) inString = false;
            }
            else if (c == '/' && j + 1 < n && css[j + 1] == '*')
            {
                var end = css.IndexOf("*/", j + 2, StringComparison.Ordinal);
                if (end < 0) return -1;
                j = end + 1;
            }
            else if (c is '"' or '\'') { inString = true; quote = c; }
            else if (c == '{') depth++;
            else if (c == '}')
            {
                if (--depth == 0) return j;
            }
        }
        return -1;
    }

    private static readonly string[] GroupingAtRuleNames = ["media", "supports", "layer", "document", "scope"];

    private static bool SelectorMatchesSomething(IDocument doc, string selector)
    {
        if (selector.Length == 0) return true;
        if (selector.Contains("::", StringComparison.Ordinal)) return true;
        try
        {
            // QuerySelector stops at the first match, unlike QuerySelectorAll which
            // enumerates every match just to test for existence.
            return doc.QuerySelector(selector) is not null;
        }
        catch
        {
            // Unparsable selector: keep the rule (the cascade ignores it too).
            return true;
        }
    }

    /// <summary>
    /// Rewrites logical margin properties into physical ones and expands every
    /// <c>margin</c> shorthand into longhands, so the stylesheet is expressed
    /// entirely in margin-top/right/bottom/left before AngleSharp parses it.
    ///
    /// Handled combinations:
    /// - <c>margin-block-start</c> / <c>margin-block-end</c> (one value each)
    /// - <c>margin-block:</c> &lt;start&gt; | &lt;start&gt; &lt;end&gt;
    /// - <c>margin:</c> &lt;top&gt; | &lt;top&gt; &lt;sides&gt; | &lt;top&gt; &lt;sides&gt; &lt;bottom&gt; | &lt;top&gt; &lt;right&gt; &lt;bottom&gt; &lt;left&gt;
    ///
    /// Invalid value counts (e.g. three values on margin-block) are left
    /// untouched and a trailing <c>!important</c> flag is preserved. After the
    /// expansion, !important margin-top/margin-bottom overrides are appended at
    /// the end of the stylesheet in document order, so expanded margins survive
    /// later generic resets (e.g. <c>p { margin: 0; }</c>) while the authors'
    /// original cascade order is kept.
    /// </summary>
    internal static string ExpandLogicalMarginsInCss(string css)
    {
        if (string.IsNullOrWhiteSpace(css)) return css;

        try
        {
            // margin-block-start -> margin-top
            css = MarginBlockStartRegex.Replace(css, m =>
            {
                var (parts, important) = SplitDeclarationValue(m.Groups[1].Value);
                return parts.Count is 1 ? $"margin-top: {parts[0]}{ImportantSuffix(important)};" : m.Value;
            });
            // margin-block-end -> margin-bottom
            css = MarginBlockEndRegex.Replace(css, m =>
            {
                var (parts, important) = SplitDeclarationValue(m.Groups[1].Value);
                return parts.Count is 1 ? $"margin-bottom: {parts[0]}{ImportantSuffix(important)};" : m.Value;
            });

            // margin-block shorthand: <start> | <start> <end>
            css = MarginBlockRegex.Replace(css, m =>
            {
                var (parts, important) = SplitDeclarationValue(m.Groups[1].Value);
                var suffix = ImportantSuffix(important);
                return parts.Count switch
                {
                    1 => $"margin-top: {parts[0]}{suffix}; margin-bottom: {parts[0]}{suffix};",
                    2 => $"margin-top: {parts[0]}{suffix}; margin-bottom: {parts[1]}{suffix};",
                    _ => m.Value
                };
            });

            // margin shorthand: <top> | <top> <sides> | <top> <sides> <bottom> | <top> <right> <bottom> <left>
            css = MarginShorthandRegex.Replace(css, m =>
            {
                var (parts, important) = SplitDeclarationValue(m.Groups[1].Value);
                var suffix = ImportantSuffix(important);
                return parts.Count switch
                {
                    1 => $"margin-top: {parts[0]}{suffix}; margin-right: {parts[0]}{suffix}; margin-bottom: {parts[0]}{suffix}; margin-left: {parts[0]}{suffix};",
                    2 => $"margin-top: {parts[0]}{suffix}; margin-right: {parts[1]}{suffix}; margin-bottom: {parts[0]}{suffix}; margin-left: {parts[1]}{suffix};",
                    3 => $"margin-top: {parts[0]}{suffix}; margin-right: {parts[1]}{suffix}; margin-bottom: {parts[2]}{suffix}; margin-left: {parts[1]}{suffix};",
                    4 => $"margin-top: {parts[0]}{suffix}; margin-right: {parts[1]}{suffix}; margin-bottom: {parts[2]}{suffix}; margin-left: {parts[3]}{suffix};",
                    _ => m.Value
                };
            });
        }
        catch
        {
            // Best-effort: if expansion fails, return the css as it is at that point
            return css;
        }

        return css;
    }

    /// <summary>
    /// Splits a declaration value at top-level whitespace (parentheses protect
    /// values such as calc()/var()) and strips a trailing !important flag.
    /// </summary>
    private static (List<string> Parts, bool Important) SplitDeclarationValue(string value)
    {
        var parts = SplitTopLevelWhitespace(value.Trim());
        var important = false;
        if (parts.Count > 0 && string.Equals(parts[^1], "!important", StringComparison.OrdinalIgnoreCase))
        {
            important = true;
            parts.RemoveAt(parts.Count - 1);
        }
        return (parts, important);
    }

    private static string ImportantSuffix(bool important) => important ? " !important" : string.Empty;

    private static string WithoutImportant(string value)
    {
        value = value.Trim();
        if (value.EndsWith("!important", StringComparison.OrdinalIgnoreCase))
            value = value.Substring(0, value.Length - "!important".Length).Trim();
        return value;
    }

    private static List<string> SplitTopLevelWhitespace(string s)
    {
        List<string> parts = [];
        if (string.IsNullOrWhiteSpace(s)) return parts;
        var depth = 0;
        var start = 0;
        for (var i = 0; i < s.Length; i++)
        {
            switch (s[i])
            {
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    break;
                default:
                {
                    if (char.IsWhiteSpace(s[i]) && depth == 0)
                    {
                        if (i > start) parts.Add(s.Substring(start, i - start).Trim());
                        start = i + 1;
                        while (start < s.Length && char.IsWhiteSpace(s[start])) start++;
                        i = start - 1;
                    }

                    break;
                }
            }
        }
        if (start < s.Length) parts.Add(s.Substring(start).Trim());
        return parts;
    }

    /// <summary>
    /// Returns the concatenated content of all &lt;style&gt; tags in the parsed document (trimmed).
    /// Useful as a basic extraction/verification helper for unit tests and the next-stage resolver.
    /// </summary>
    public async Task<string> GetStyleTagContentAsync(string htmlFragment, string[]? css = null)
    {
        var doc = await ParseDocumentAsync(htmlFragment, css);
        var styles = doc.QuerySelectorAll("style");
        return string.Join('\n', styles.Select(s => s.TextContent.Trim())).Trim();
    }
    
    private string ExpandedCssCacheGetOrAdd(string css)
    {
        if (_expandedCssCache.TryGetValue(css, out var expanded)) return expanded;
        expanded = ExpandLogicalMarginsInCss(css);
        _expandedCssCache[css] = expanded;
        return expanded;
    }

    private static string BuildGlobalCss(PageCalculatorOptions o)
    {
        var ci = CultureInfo.InvariantCulture;
        return $$"""
                 html {
                     --font-size: {{o.FontSize.ToString("0.00", ci)}};
                     --line-height: {{o.LineHeight.ToString("0.00", ci)}};
                     --letter-spacing: {{o.LetterSpacing.ToString("0.00", ci)}};
                     --word-spacing: {{o.WordSpacing.ToString("0.00", ci)}};
                     --paragraph-spacing: {{o.ParagraphSpacing.ToString("0.00", ci)}};
                     --text-indent: {{o.TextIndent.ToString("0.00", ci)}};
                     --margin-vertical: {{o.VerticalMargin.ToString("0.00", ci)}};
                     --margin-horizontal: {{o.HorizontalMargin.ToString("0.00", ci)}};
                     --page-gap: var(--margin-horizontal);
                     --page-height: calc({{o.PageHeightPx}} * 1px);
                     --page-width: calc({{o.PageWidthPx}} * 1px);
                     
                     font-size: calc({{o.RootFontSizePx.ToString("0.00", ci)}} * 1px);
                     height: var(--page-height);
                     width: var(--page-width);
                     margin: calc(var(--margin-vertical) * 1rem) calc(var(--margin-horizontal) * 1rem);
                     padding: 0 !important;
                     overflow: hidden;
                     display: flex;
                 }

                 body {
                     flex: 1;
                     margin: 0 !important;
                     padding: 0 !important;
                     height: var(--page-height);
                     width: var(--page-width);
                     column-width: var(--page-width);
                     column-fill: auto;
                     column-gap: calc(var(--page-gap) * 1rem);
                     font-size: calc(var(--font-size) * 1px) !important;
                     letter-spacing: calc(var(--letter-spacing) * 1px);
                     word-spacing: calc(var(--word-spacing) * 1px);
                     orphans: 1;
                     widows: 1;
                 }

                 p {
                     word-break: auto-phrase !important;
                     hyphens: none !important;
                 }

                 body p:not([class]) {
                     margin-block: calc(var(--paragraph-spacing) * 1pt);
                 }

                 img, svg, image, figure {
                     box-sizing: border-box;
                     object-fit: contain !important;
                     break-inside: avoid !important;
                     max-width: 100%;
                     max-height: var(--page-height) !important;
                     float: none !important;
                 }

                 sup {
                     font-size: 0.6em;
                     line-height: normal;
                     font-weight: bold;
                 }

                 .drop-cap {
                     orphans: 2;
                     display: flow-root;
                 }

                 .drop-cap::first-letter {
                     initial-letter: 2;
                     margin-inline-end: 0.3em;
                 }

                 .drop-cap + p {
                     clear: both;
                 }
                 """;
    }
}
