using System.Globalization;
using System.Text;
using BookHeaven.Core.DOM.CSS;
using BookHeaven.Core.DOM.HTML.Models;
using BookHeaven.Core.DOM.Services;

namespace BookHeaven.Core.DOM.HTML;

/// <summary>
/// A minimal, fast HTML-to-DOM parser for the block layout engine. It builds the
/// <see cref="DomDocument"/> tree the engine needs (elements, attributes, text) in a
/// single pass, with none of AngleSharp's HTML5 tree-construction, namespace or
/// error-recovery machinery.
///
/// The parser owns the HTML construction: it injects the global stylesheet and the
/// chapter stylesheets as &lt;style&gt; tags around the fragment, parses the result,
/// and returns the document plus the parsed CSS rules. The layout engine only passes
/// the fragment and the styles down and never touches the HTML.
///
/// Scope: well-formed XHTML (the EPUB case) plus graceful degradation for the common
/// malformations (unclosed tags, stray close tags). Void elements are recognised,
/// entities are decoded, and comments/DOCTYPE/processing instructions are dropped.
/// </summary>
public sealed class HtmlParser
{
    private readonly PageCalculatorOptions _options;

    public HtmlParser(PageCalculatorOptions options)
    {
        _options = options ?? new PageCalculatorOptions();
    }

    // HTML void elements: no content, no closing tag.
    private static readonly HashSet<string> VoidTags =
    [
        "area", "base", "br", "col", "embed", "hr", "img", "input",
        "link", "meta", "param", "source", "track", "wbr"
    ];

    // The named entities that actually occur in EPUB content. Numeric entities
    // (&#nn; / &#xhh;) are handled separately.
    private static readonly Dictionary<string, string> NamedEntities = new()
    {
        ["amp"] = "&", ["lt"] = "<", ["gt"] = ">", ["quot"] = "\"", ["apos"] = "'",
        ["nbsp"] = "\u00A0", ["copy"] = "\u00A9", ["reg"] = "\u00AE", ["trade"] = "\u2122",
        ["hellip"] = "\u2026", ["mdash"] = "\u2014", ["ndash"] = "\u2013",
        ["lsquo"] = "\u2018", ["rsquo"] = "\u2019", ["ldquo"] = "\u201C", ["rdquo"] = "\u201D",
        ["laquo"] = "\u00AB", ["raquo"] = "\u00BB", ["times"] = "\u00D7", ["divide"] = "\u00F7",
        ["deg"] = "\u00B0", ["plusmn"] = "\u00B1", ["sup2"] = "\u00B2", ["sup3"] = "\u00B3",
        ["frac12"] = "\u00BD", ["frac14"] = "\u00BC", ["frac34"] = "\u00BE",
        ["euro"] = "\u20AC", ["pound"] = "\u00A3", ["cent"] = "\u00A2", ["yen"] = "\u00A5",
    };

    /// <summary>
    /// Builds the full document (global stylesheet + chapter stylesheets as
    /// &lt;style&gt; tags + the fragment), parses it, and returns the document plus
    /// the parsed CSS rules. The fragment is passed as-is: the parser wraps it in its
    /// own synthetic html/body, so the <c>html</c> and <c>body</c> rules (custom
    /// properties, font-size) match the wrapping elements and <c>var()</c> references
    /// resolve through inheritance.
    /// </summary>
    public (DomDocument Doc, List<MiniCssRule> Rules) Parse(string htmlFragment, IReadOnlyList<string>? css = null)
    {
        var doc = BuildDocument(htmlFragment, css);

        // Collect <style> from the whole document, not just the head: a full-document
        // fragment has its head foster-parented into the body, so its inline styles
        // (and the injected global/chapter sheets) can live anywhere in the tree.
        var rules = new List<MiniCssRule>();
        foreach (var styleEl in CollectStyleElements(doc.Root))
        {
            var text = styleEl.TextContent;
            if (string.IsNullOrWhiteSpace(text)) continue;
            rules.AddRange(CssParser.Parse(text));
        }
        return (doc, rules);
    }

    /// <summary>
    /// Parses an HTML fragment into a document: an <c>html</c> root with a <c>body</c>
    /// child that holds the fragment. The <c>html</c> element matters because the
    /// global stylesheet declares custom properties and font-size on it, which
    /// <c>body</c> inherits.
    /// </summary>
    private DomDocument BuildDocument(string htmlFragment, IReadOnlyList<string>? css = null)
    {
        var htmlEl = new Element("html");
        var head = new Element("head") { Parent = htmlEl };
        
        var defaultCss = BuildGlobalCss(_options);
        var injectedCssStyle = new Element("style") { Parent = head };
        injectedCssStyle.ChildNodes.Add(new Models.Text(defaultCss) { Parent = injectedCssStyle });
        head.ChildNodes.Add(injectedCssStyle);
        if (css?.Count > 0)
        {
            foreach (var style in css ?? [])
            {
                var styleElement = new Element("style") { Parent = head };
                styleElement.ChildNodes.Add(new Models.Text(style) { Parent = styleElement });
                head.ChildNodes.Add(styleElement);
            }
        }
        htmlEl.ChildNodes.Add(head);
        
        var body = new Element("body") { Parent = htmlEl };
        
        htmlEl.ChildNodes.Add(body);
        var stack = new Stack<Element>(64);
        stack.Push(body);

        var n = htmlFragment.Length;
        var i = 0;
        var textStart = -1;

        while (i < n)
        {
            if (htmlFragment[i] != '<')
            {
                if (textStart < 0) textStart = i;
                i++;
                continue;
            }

            // A '<' begins a tag (or comment/PI/DOCTYPE). Flush any pending text first.
            if (textStart >= 0)
            {
                AppendText(htmlFragment, textStart, i, stack.Peek());
                textStart = -1;
            }

            var next = i + 1 < n ? htmlFragment[i + 1] : '\0';
            if (next == '!' || next == '?')
            {
                // Comment, DOCTYPE or processing instruction: skip to the closing '>'.
                var close = htmlFragment.IndexOf('>', i + 2);
                i = close < 0 ? n : close + 1;
                continue;
            }

            if (next == '/')
            {
                // Closing tag.
                var close = htmlFragment.IndexOf('>', i + 2);
                if (close < 0) { i = n; continue; }
                var closeTag = NormalizeTag(htmlFragment.AsSpan(i + 2, close - i - 2));
                PopToMatch(stack, body, closeTag);
                i = close + 1;
                continue;
            }

            // Opening tag.
            var openClose = htmlFragment.IndexOf('>', i + 1);
            if (openClose < 0) { i = n; continue; }
            var tagSpan = htmlFragment.AsSpan(i + 1, openClose - i - 1);
            var (tag, bodyStart) = ParseTagName(tagSpan);

            // The synthetic wrapper already provides html/head/body. A full-document
            // fragment repeats them; a browser ignores those start tags in body context
            // (foster-parenting their content into the body), so we skip the tag itself.
            // The matching close tags are no-ops via PopToMatch.
            if (tag is "html" or "head" or "body")
            {
                i = openClose + 1;
                continue;
            }

            var el = new Element(tag);
            ParseAttributes(tagSpan, bodyStart, el, out var selfClosing);

            var parent = stack.Peek();
            el.Parent = parent;
            parent.ChildNodes.Add(el);

            if (!selfClosing && !VoidTags.Contains(tag))
                stack.Push(el);

            i = openClose + 1;
        }

        if (textStart >= 0)
            AppendText(htmlFragment, textStart, n, stack.Peek());

        return new DomDocument(htmlEl);
    }

    /// <summary>
    /// The global stylesheet injected before the chapter stylesheets. It declares the
    /// custom properties the chapter CSS references via <c>var()</c> and the page box
    /// geometry. Moved here from the engine so the parser owns the full HTML.
    /// </summary>
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

                 // Minimal UA stylesheet: the chapter CSS assumes browser defaults
                 // (em is italic, b/strong is bold). Injected first so any chapter
                 // rule overrides it, exactly like a real UA sheet.
                 em, i, cite, dfn, var {
                     font-style: italic;
                 }

                 b, strong {
                     font-weight: bold;
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

    /// <summary>Collects every <c>style</c> element in the subtree (pre-order).</summary>
    private static List<Element> CollectStyleElements(Element root)
    {
        var result = new List<Element>();
        Collect(root);
        return result;

        void Collect(Element el)
        {
            if (el.TagName == "style") result.Add(el);
            foreach (var child in el.Children) Collect(child);
        }
    }

    /// <summary>
    /// On a close tag, pop the stack back to the matching open element. If the tag is not
    /// on the stack (stray/mismatched close), it is ignored — the open elements stay put,
    /// so the tree degrades gracefully instead of corrupting.
    /// </summary>
    private static void PopToMatch(Stack<Element> stack, Element body, string tag)
    {
        while (stack.Count > 1)
        {
            var top = stack.Peek();
            if (top.TagName == tag)
            {
                stack.Pop();
                return;
            }
            stack.Pop();
        }
        // No match down to the body: ignore the close tag.
    }

    private static string NormalizeTag(ReadOnlySpan<char> span)
    {
        // Trim and lowercase; tag names are case-insensitive in HTML.
        var s = span.ToString().Trim();
        return s.Length == 0 ? s : s.ToLowerInvariant();
    }

    /// <summary>
    /// Parses the tag name (up to the first whitespace, '/' or end) of a tag body
    /// (the text between '<' and '>'), working directly on the span so no intermediate
    /// string is allocated. Returns the lowercase name and the index where the
    /// attribute body starts.
    /// </summary>
    private static (string Tag, int BodyStart) ParseTagName(ReadOnlySpan<char> span)
    {
        var i = 0;
        while (i < span.Length && !char.IsWhiteSpace(span[i]) && span[i] != '/')
            i++;
        var tag = span[..i].ToString().ToLowerInvariant();
        return (tag, i);
    }

    /// <summary>
    /// Parses the attribute body of a tag directly into the element's attribute store
    /// (no intermediate list). Attribute names are lowercased; values are entity-decoded.
    /// A trailing '/' sets <paramref name="selfClosing"/>.
    /// </summary>
    private static void ParseAttributes(ReadOnlySpan<char> span, int start, Element el, out bool selfClosing)
    {
        var n = span.Length;
        var i = start;
        selfClosing = false;

        while (i < n)
        {
            while (i < n && char.IsWhiteSpace(span[i])) i++;
            if (i >= n) break;

            if (span[i] == '/')
            {
                selfClosing = true;
                i++;
                continue;
            }

            // Attribute name.
            var nameStart = i;
            while (i < n && span[i] != '=' && !char.IsWhiteSpace(span[i]) && span[i] != '/')
                i++;
            if (i == nameStart) continue;
            var name = span[nameStart..i].ToString().ToLowerInvariant();

            // Optional value.
            while (i < n && char.IsWhiteSpace(span[i])) i++;
            string value;
            if (i < n && span[i] == '=')
            {
                i++;
                while (i < n && char.IsWhiteSpace(span[i])) i++;
                if (i < n && (span[i] == '"' || span[i] == '\''))
                {
                    var quote = span[i];
                    i++;
                    var valStart = i;
                    while (i < n && span[i] != quote) i++;
                    value = DecodeEntities(span[valStart..i]);
                    if (i < n) i++; // consume the closing quote
                }
                else
                {
                    var valStart = i;
                    while (i < n && !char.IsWhiteSpace(span[i])) i++;
                    value = DecodeEntities(span[valStart..i]);
                }
            }
            else
            {
                value = string.Empty;
            }

            el.SetAttribute(name, value);
        }
    }

    /// <summary>Appends the text span [start, end) to <paramref name="parent"/>, decoding entities.</summary>
    private static void AppendText(string html, int start, int end, Element parent)
    {
        if (end <= start) return;
        var text = DecodeEntities(html.AsSpan(start, end - start));
        if (text.Length == 0) return;
        var node = new Models.Text(text);
        node.Parent = parent;
        parent.ChildNodes.Add(node);
    }

    /// <summary>Decodes HTML entities (&amp;, &lt;, &#nn;, &#xhh;, ...) in <paramref name="text"/>.</summary>
    public static string DecodeEntities(string text)
        => text.Contains('&') ? DecodeEntities(text.AsSpan()) : text;

    /// <summary>Decodes HTML entities in a span; returns the original text (no copy) when it has none.</summary>
    public static string DecodeEntities(ReadOnlySpan<char> text)
    {
        if (!text.Contains('&')) return text.ToString();

        var sb = new StringBuilder(text.Length);
        var i = 0;
        var n = text.Length;
        while (i < n)
        {
            if (text[i] != '&')
            {
                sb.Append(text[i]);
                i++;
                continue;
            }

            var semiRel = text.Slice(i + 1).IndexOf(';');
            var semi = semiRel < 0 ? -1 : i + 1 + semiRel;
            if (semi > i && semi - i <= 11 && TryDecodeEntity(text.Slice(i + 1, semi - i - 1), out var decoded))
            {
                sb.Append(decoded);
                i = semi + 1;
                continue;
            }

            sb.Append(text[i]);
            i++;
        }
        return sb.ToString();
    }

    private static bool TryDecodeEntity(ReadOnlySpan<char> entity, out string decoded)
    {
        decoded = string.Empty;
        if (entity.Length == 0) return false;

        // Numeric: &#123; or &#x1F;
        if (entity[0] == '#')
        {
            var body = entity.Slice(1);
            var code = ParseCodePoint(body);
            if (code is > 0 and <= 0x10FFFF)
            {
                decoded = char.ConvertFromUtf32(code);
                return true;
            }
            return false;
        }

        // Named entity (strip the leading '&').
        if (NamedEntities.TryGetValue(entity.Slice(1).ToString(), out var value))
        {
            decoded = value!;
            return true;
        }
        return false;
    }

    private static int ParseCodePoint(ReadOnlySpan<char> body)
    {
        if (body.Length == 0) return -1;
        var isHex = body[0] == 'x' || body[0] == 'X';
        var start = isHex ? 1 : 0;
        var value = 0;
        for (var i = start; i < body.Length; i++)
        {
            var c = body[i];
            int digit;
            if (c >= '0' && c <= '9') digit = c - '0';
            else if (c >= 'a' && c <= 'f') digit = c - 'a' + 10;
            else if (c >= 'A' && c <= 'F') digit = c - 'A' + 10;
            else return -1;
            value = value * (isHex ? 16 : 10) + digit;
        }
        return value;
    }
}
