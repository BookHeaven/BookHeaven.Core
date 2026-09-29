using System.Text;
using BookHeaven.Core.DOM.CSS;
using BookHeaven.Core.DOM.CSS.Models;
using BookHeaven.Core.DOM.HTML.Models;
using BookHeaven.Core.DOM.Text.Abstractions;
using BookHeaven.Core.DOM.Text.Enums;
using BookHeaven.Core.DOM.Text.Helpers;

namespace BookHeaven.Core.DOM.Engine.Helpers;

/// <summary>
/// Splits an element's inline content into <see cref="TextRun"/>s at their
/// effective font size and style (browser model): each inline element's
/// explicitly declared font properties apply to its subtree, and consecutive
/// runs at the same size AND style are merged.
/// </summary>
public static class TextRunBuilder
{
    /// <summary>Maps a bold/italic pair to the measurer's <see cref="FontStyle"/>.</summary>
    public static FontStyle ToFontStyle(bool bold, bool italic) =>
        (bold, italic) switch
        {
            (true, true) => FontStyle.BoldItalic,
            (true, false) => FontStyle.Bold,
            (false, true) => FontStyle.Italic,
            _ => FontStyle.Regular
        };

    /// <summary>
    /// Splits the element's inline content into <see cref="TextRun"/>s at their
    /// effective font size and style. Consecutive runs at the same size AND
    /// style are merged. <paramref name="text"/> is the caller's precomputed
    /// text for the uniform fast path: for a leaf it is <c>TrimmedTextContent</c>;
    /// for a container's own inline content it is <see cref="GetOwnInlineText"/>
    /// (the whole-subtree text would double-count the block children).
    /// </summary>
    public static List<TextRun> Build(
        Element el, string text, float ownFontSize, bool ownBold, bool ownItalic,
        Func<Element, CssStyle> getComputedStyle,
        Func<string?, float, float?, float?> parseLength,
        Func<string, bool> isBlockLevelTag)
    {
        if (IsUniformInline(el, ownFontSize, ownBold, ownItalic, getComputedStyle, parseLength, isBlockLevelTag))
        {
            // The measurer collapses leading/trailing whitespace anyway, and
            // reusing the caller's text avoids a second subtree walk.
            return [new TextRun(text, ownFontSize, ToFontStyle(ownBold, ownItalic))];
        }

        var builders = new List<RunBuilder>();
        WalkInlineRuns(el, ownFontSize, ownBold, ownItalic, builders, getComputedStyle, parseLength, isBlockLevelTag);
        var runs = new List<TextRun>(builders.Count);
        foreach (var b in builders) runs.Add(b.Build());
        return runs;
    }

    /// <summary>
    /// True when every inline descendant renders at the inherited size and style,
    /// so the whole element is a single run and the run walk can be skipped.
    /// </summary>
    private static bool IsUniformInline(
        Element element, float inheritedSize, bool inheritedBold, bool inheritedItalic,
        Func<Element, CssStyle> getComputedStyle,
        Func<string?, float, float?, float?> parseLength,
        Func<string, bool> isBlockLevelTag)
    {
        foreach (var node in element.ChildNodes)
        {
            if (node is not Element childEl || isBlockLevelTag(childEl.TagName)) continue;

            var (childSize, childBold, childItalic) =
                ResolveInlineFont(getComputedStyle(childEl), inheritedSize, inheritedBold, inheritedItalic, parseLength);

            if (Math.Abs(childSize - inheritedSize) >= 0.001f || childBold != inheritedBold || childItalic != inheritedItalic)
                return false;

            if (!IsUniformInline(childEl, childSize, childBold, childItalic, getComputedStyle, parseLength, isBlockLevelTag))
                return false;
        }

        return true;
    }

    private static void WalkInlineRuns(
        Element element, float inheritedSize, bool inheritedBold, bool inheritedItalic, List<RunBuilder> runs,
        Func<Element, CssStyle> getComputedStyle,
        Func<string?, float, float?, float?> parseLength,
        Func<string, bool> isBlockLevelTag)
    {
        foreach (var node in element.ChildNodes)
        {
            if (node.NodeType == NodeType.Text)
            {
                AppendRun(runs, node.TextContent, inheritedSize, inheritedBold, inheritedItalic);
            }
            else if (node is Element childEl && isBlockLevelTag(childEl.TagName))
            {
                // Defensive: leaf blocks have no block children; a container's block
                // children become sibling blocks, not part of its own inline text.
            }
            else if (node is Element inline)
            {
                var (childSize, childBold, childItalic) =
                    ResolveInlineFont(getComputedStyle(inline), inheritedSize, inheritedBold, inheritedItalic, parseLength);
                WalkInlineRuns(inline, childSize, childBold, childItalic, runs, getComputedStyle, parseLength, isBlockLevelTag);
            }
        }
    }

    /// <summary>
    /// Resolves one inline element's effective font: only an EXPLICITLY declared
    /// font-size is re-parsed (an inherited raw string, e.g. "max(2.42em, …)",
    /// would apply the em unit twice); weight/style fall back to the inherited
    /// values when undeclared.
    /// </summary>
    private static (float Size, bool Bold, bool Italic) ResolveInlineFont(
        CssStyle style, float inheritedSize, bool inheritedBold, bool inheritedItalic,
        Func<string?, float, float?, float?> parseLength)
    {
        var size = inheritedSize;
        var bold = inheritedBold;
        var italic = inheritedItalic;

        var fs = style.GetPropertyValue("font-size");
        if (!string.IsNullOrWhiteSpace(fs) && style.IsExplicit("font-size"))
        {
            var parsed = parseLength(fs, size, size);
            if (parsed is > 0f) size = parsed.Value;
        }
        var fw = style.GetPropertyValue("font-weight");
        if (!string.IsNullOrWhiteSpace(fw)) bold = CssValueParser.IsBoldWeight(fw);
        var fst = style.GetPropertyValue("font-style");
        if (!string.IsNullOrWhiteSpace(fst)) italic = CssValueParser.IsItalicStyle(fst);

        return (size, bold, italic);
    }

    private static void AppendRun(List<RunBuilder> runs, string text, float size, bool bold, bool italic)
    {
        if (text.Length == 0) return;
        var style = ToFontStyle(bold, italic);
        if (runs.Count > 0 && Math.Abs(runs[^1].Size - size) < 0.001f && runs[^1].Style == style)
        {
            runs[^1].Append(text);
        }
        else
        {
            runs.Add(new RunBuilder { Size = size, Style = style });
            runs[^1].Append(text);
        }
    }

    /// <summary>
    /// Accumulates the text of a single run in a <see cref="StringBuilder"/> so that
    /// merging consecutive same-style fragments is O(total text).
    /// </summary>
    private sealed class RunBuilder
    {
        public float Size;
        public FontStyle Style;
        private readonly StringBuilder _text = new();

        public void Append(string text) => _text.Append(text);

        public TextRun Build() => new(_text.ToString(), Size, Style);
    }

    /// <summary>Extracts text content that belongs directly to the element (not inside block-level children).</summary>
    public static string GetOwnInlineText(Element el, List<Element> blockChildren)
    {
        var sb = new StringBuilder();
        foreach (var node in el.ChildNodes)
        {
            if (node.NodeType == NodeType.Text)
            {
                sb.Append(node.TextContent);
            }
            else if (node is Element childEl && !blockChildren.Contains(childEl))
            {
                // Inline element (span, em, strong, a, etc.) - include its text.
                Element.AppendDescendantText(childEl, sb);
            }
            // Block children are skipped (their text is handled by recursion).
        }

        // Trim only CSS-collapsible whitespace: a non-collapsible character
        // (e.g. U+00A0) survives the trim and makes the text non-empty, so the
        // engine emits the anonymous block the browser renders for it.
        var start = 0;
        var end = sb.Length;
        while (start < end && TextRunWrapper.IsCollapsibleWhitespace(sb[start])) start++;
        while (end > start && TextRunWrapper.IsCollapsibleWhitespace(sb[end - 1])) end--;
        return sb.ToString(start, end - start);
    }
}
