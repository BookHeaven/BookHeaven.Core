using System.Text;
using BookHeaven.Core.DOM.CSS;
using BookHeaven.Core.DOM.CSS.Models;
using BookHeaven.Core.DOM.HTML.Models;
using BookHeaven.Core.DOM.Text.Enums;
using BookHeaven.Core.DOM.Text.Models;

namespace BookHeaven.Core.DOM.Text.Helpers;

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
    /// The element's top-level inline content in document order: text nodes and
    /// visible inline elements. Hidden elements and block children are excluded
    /// (the latter become sibling blocks, not inline content); inline-display
    /// images are INCLUDED even when the tag heuristic calls them block — they
    /// are part of the inline content (baseline model).
    /// </summary>
    public static List<Node> CollectInlineFragments(Element el, Func<Element, bool> isBlockChild, Func<Element, bool> isInlineImage, Func<Element, bool> isHidden)
    {
        var fragments = new List<Node>();
        foreach (var node in el.ChildNodes)
        {
            if (node.NodeType == NodeType.Text)
            {
                fragments.Add(node);
            }
            else if (node is Element childEl && !isHidden(childEl) && (!isBlockChild(childEl) || isInlineImage(childEl)))
            {
                fragments.Add(childEl);
            }
        }
        return fragments;
    }

    /// <summary>
    /// Splits a specific set of top-level inline nodes (document order) into
    /// <see cref="TextRun"/>s. A browser's anonymous box holds only the inline
    /// content of its own segment, so the runs must be built from the segment's
    /// nodes, not the whole element.
    /// </summary>
    public static List<TextRun> BuildFragments(
        IReadOnlyList<Node> fragments, string text, float ownFontSize, bool ownBold, bool ownItalic,
        Func<Element, CssStyle> getComputedStyle,
        Func<string?, float, float?, float?> parseLength,
        Func<Element, bool> isBlockChild,
        Func<Element, bool> isHidden)
    {
        if (IsUniformFragments(fragments, ownFontSize, ownBold, ownItalic, getComputedStyle, parseLength, isBlockChild, isHidden))
        {
            // The measurer collapses leading/trailing whitespace anyway, and
            // reusing the caller's text avoids a second subtree walk.
            return [new TextRun(text, ownFontSize, ToFontStyle(ownBold, ownItalic))];
        }

        var builders = new List<RunBuilder>();
        WalkFragments(fragments, ownFontSize, ownBold, ownItalic, builders, getComputedStyle, parseLength, isBlockChild, isHidden);
        var runs = new List<TextRun>(builders.Count);
        foreach (var b in builders) runs.Add(b.Build());
        return runs;
    }

    /// <summary>
    /// True when every inline descendant of the fragments renders at the inherited
    /// size and style, so the whole content is a single run and the run walk can be
    /// skipped.
    /// </summary>
    private static bool IsUniformFragments(
        IReadOnlyList<Node> fragments, float inheritedSize, bool inheritedBold, bool inheritedItalic,
        Func<Element, CssStyle> getComputedStyle,
        Func<string?, float, float?, float?> parseLength,
        Func<Element, bool> isBlockChild,
        Func<Element, bool> isHidden)
    {
        foreach (var node in fragments)
        {
            if (node is not Element childEl || isHidden(childEl) || isBlockChild(childEl)) continue;

            var (childSize, childBold, childItalic) =
                ResolveInlineFont(getComputedStyle(childEl), inheritedSize, inheritedBold, inheritedItalic, parseLength);

            if (Math.Abs(childSize - inheritedSize) >= 0.001f || childBold != inheritedBold || childItalic != inheritedItalic)
                return false;

            if (!IsUniformFragments(childEl.ChildNodes, childSize, childBold, childItalic, getComputedStyle, parseLength, isBlockChild, isHidden))
                return false;
        }

        return true;
    }

    private static void WalkFragments(
        IReadOnlyList<Node> fragments, float inheritedSize, bool inheritedBold, bool inheritedItalic, List<RunBuilder> runs,
        Func<Element, CssStyle> getComputedStyle,
        Func<string?, float, float?, float?> parseLength,
        Func<Element, bool> isBlockChild,
        Func<Element, bool> isHidden)
    {
        foreach (var node in fragments)
        {
            if (node.NodeType == NodeType.Text)
            {
                AppendRun(runs, node.TextContent, inheritedSize, inheritedBold, inheritedItalic);
            }
            else if (node is Element childEl)
            {
                if (isHidden(childEl) || isBlockChild(childEl))
                {
                    // Hidden elements contribute nothing; a container's block children
                    // become sibling blocks, not part of its own inline text.
                }
                else
                {
                    var (childSize, childBold, childItalic) =
                        ResolveInlineFont(getComputedStyle(childEl), inheritedSize, inheritedBold, inheritedItalic, parseLength);
                    WalkInlineRuns(childEl, childSize, childBold, childItalic, runs, getComputedStyle, parseLength, isBlockChild, isHidden);
                }
            }
        }
    }

    private static void WalkInlineRuns(
        Element element, float inheritedSize, bool inheritedBold, bool inheritedItalic, List<RunBuilder> runs,
        Func<Element, CssStyle> getComputedStyle,
        Func<string?, float, float?, float?> parseLength,
        Func<Element, bool> isBlockChild,
        Func<Element, bool> isHidden)
    {
        WalkFragments(element.ChildNodes, inheritedSize, inheritedBold, inheritedItalic, runs, getComputedStyle, parseLength, isBlockChild, isHidden);
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
            // Keywords (small, larger, xx-large, …) are ratios of the inherited size.
            if (CssValueParser.TryParseFontSizeKeyword(fs, out var ratio))
                size *= ratio;
            else
            {
                var parsed = parseLength(fs, size, size);
                if (parsed is > 0f) size = parsed.Value;
            }
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

}
