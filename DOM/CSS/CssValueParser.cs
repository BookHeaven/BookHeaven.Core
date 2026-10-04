using System.Globalization;
using System.Text.RegularExpressions;
using BookHeaven.Core.DOM.CSS.Models;

namespace BookHeaven.Core.DOM.CSS;

/// <summary>
/// Pure parsers for the CSS property values the layout engine consumes: font
/// weight/style, line-height form, display, break properties, initial-letter,
/// hidden detection and spacing lengths. Everything here is a function of the
/// value string (and, for lengths, the parse context) — no engine state.
/// </summary>
public static partial class CssValueParser
{
    /// <summary>CSS <c>font-weight</c>: bold at "bold"/"bolder" or a numeric weight ≥ 600.</summary>
    public static bool IsBoldWeight(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var v = value.Trim().ToLowerInvariant();
        if (v is "bold" or "bolder") return true;
        return float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && n >= 600f;
    }

    /// <summary>
    /// CSS <c>font-size</c> keywords as a ratio against the PARENT's font size.
    /// Absolute keywords (CSS 2.1 §15.2): xx-small 3/5, x-small 3/4, small 8/10,
    /// medium 1, large 10/8, x-large 4/3, xx-large 5/3. Relative keywords step one
    /// size: smaller 3/4, larger 4/3. Returns false for lengths.
    /// </summary>
    public static bool TryParseFontSizeKeyword(string? value, out float ratio)
    {
        ratio = 1f;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var v = value.Trim().ToLowerInvariant();
        return v switch
        {
            "xx-small" => SetRatio(ref ratio, 3f / 5f),
            "x-small" => SetRatio(ref ratio, 3f / 4f),
            "small" => SetRatio(ref ratio, 8f / 10f),
            "medium" => SetRatio(ref ratio, 1f),
            "large" => SetRatio(ref ratio, 10f / 8f),
            "x-large" => SetRatio(ref ratio, 4f / 3f),
            "xx-large" => SetRatio(ref ratio, 5f / 3f),
            "smaller" => SetRatio(ref ratio, 3f / 4f),
            "larger" => SetRatio(ref ratio, 4f / 3f),
            _ => false
        };

        static bool SetRatio(ref float r, float value)
        {
            r = value;
            return true;
        }
    }

    /// <summary>CSS <c>font-style</c>: italic at "italic"/"oblique".</summary>
    public static bool IsItalicStyle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var v = value.Trim().ToLowerInvariant();
        return v is "italic" or "oblique";
    }

    /// <summary>
    /// Parses the <c>line-height</c> keeping the LENGTH and UNITLESS forms separate:
    /// a unitless line-height multiplies each inline's OWN font size (browser model).
    /// </summary>
    public static (float? LengthPx, float? Multiplier) ResolveLineHeightForm(
        string? value, float fontSize,
        Func<string?, float, float?, float?> parseLength,
        Func<string, float?> parseUnitless)
    {
        float? lengthPx = null;
        float? multiplier = null;
        if (!string.IsNullOrWhiteSpace(value))
        {
            var trimmed = value.Trim();
            if (float.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var unitless))
                multiplier = unitless;
            else
            {
                var parsed = parseLength(trimmed, fontSize, fontSize);
                if (parsed.HasValue) lengthPx = parsed.Value;
                else if (parseUnitless(trimmed) is { } u) multiplier = u;
            }
        }
        return (lengthPx, multiplier);
    }

    /// <summary>
    /// Parses a spacing length (<c>letter-spacing</c>, <c>word-spacing</c>): "normal"
    /// is 0, a parseable length is its px value, anything else is null (unspecified).
    /// </summary>
    public static float? TryParseSpacing(
        string? value, float reference, float? emReference,
        Func<string?, float, float?, float?> parseLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        if (trimmed.Equals("normal", StringComparison.OrdinalIgnoreCase)) return 0f;
        return parseLength(trimmed, reference, emReference);
    }

    /// <summary>
    /// Parses the CSS break properties. Only <c>break-before/after: column</c> is
    /// honored: the page count is derived from a CSS multi-column layout
    /// (column-fill: auto), and Chromium treats break-before/after: page (and the
    /// legacy page-break-*: always) as print-only — a no-op in columns, where
    /// content flows greedily (verified: 5 forced-break divs pack 2 per column,
    /// .browser-test/break_test.html). Honoring the page value would inflate the
    /// count (e.g. arcanum ch146: 46 vs the browser's 21).
    /// <c>break-inside: avoid</c> (and the legacy <c>page-break-inside: avoid</c>)
    /// IS honored (Chromium avoids breaking inside the box).
    /// <c>break-after: avoid</c> (and the legacy <c>page-break-after: avoid</c>)
    /// IS honored: a block that would end up as the last one on a page is moved to
    /// the next page so the break does not land right after it.
    /// </summary>
    public static (bool PageBreakBefore, bool PageBreakAfter, bool AvoidBreakInside, bool AvoidBreakAfter) ParseBreaks(CssStyle style)
    {
        var pageBreakBefore = EqualsValue(style.GetPropertyValue("break-before"), "column");
        var pageBreakAfter = EqualsValue(style.GetPropertyValue("break-after"), "column");
        var avoidBreakInside = EqualsValue(style.GetPropertyValue("page-break-inside"), "avoid")
            || EqualsValue(style.GetPropertyValue("break-inside"), "avoid");
        var avoidBreakAfter = EqualsValue(style.GetPropertyValue("page-break-after"), "avoid")
            || EqualsValue(style.GetPropertyValue("break-after"), "avoid");
        return (pageBreakBefore, pageBreakAfter, avoidBreakInside, avoidBreakAfter);
    }

    /// <summary>Parses an <c>initial-letter</c> value into a line count, or null.</summary>
    public static int? ParseInitialLetter(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (float.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0f)
            return (int)Math.Ceiling(v);
        return null;
    }

    /// <summary>
    /// True when an element is hidden: a <c>hidden</c> attribute, an inline
    /// <c>display:none</c>, or a computed <c>display: none</c>.
    /// </summary>
    public static bool IsHidden(bool hasHiddenAttribute, string? inlineStyle, string? display)
    {
        if (hasHiddenAttribute) return true;
        if (!string.IsNullOrWhiteSpace(inlineStyle) && DisplayNoneRegex().IsMatch(inlineStyle))
            return true;
        return EqualsValue(display, "none");
    }

    /// <summary>
    /// True when the computed <c>display</c> is block-like. Images are inline by
    /// UA default; <c>figure</c> is block.
    /// </summary>
    // display values are trimmed at parse time (CssParser/ParseInline), so a direct
    // case-insensitive set lookup is allocation-free (the old Trim().ToLowerInvariant()
    // allocated a string per call on the per-element hot path).
    private static readonly HashSet<string> InlineDisplayValues =
        new(StringComparer.OrdinalIgnoreCase) { "inline", "inline-block", "inline-flex", "inline-grid", "none" };

    public static bool IsBlockDisplay(string? display, string tagName)
    {
        if (string.IsNullOrWhiteSpace(display))
            return tagName.Equals("figure", StringComparison.OrdinalIgnoreCase);
        return !InlineDisplayValues.Contains(display);
    }

    private static bool EqualsValue(string? value, string expected)
        => !string.IsNullOrWhiteSpace(value) && value.Trim().Equals(expected, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"display\s*:\s*none", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex DisplayNoneRegex();
}
