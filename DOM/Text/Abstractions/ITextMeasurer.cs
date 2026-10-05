using BookHeaven.Core.DOM.Text.Enums;
using BookHeaven.Core.DOM.Text.Models;
using SkiaSharp;

namespace BookHeaven.Core.DOM.Text.Abstractions;

public interface ITextMeasurer
{
    /// <summary>
    /// The "normal" line-height approximation used when the CSS declares no
    /// <c>line-height</c> (a browser derives it from the font metrics; 1.2 × font
    /// size is the standard estimate). Shared by the measurers, the run wrapper and
    /// the layout engine's spacer geometry so they can never drift apart.
    /// </summary>
    const float NormalLineHeightMultiplier = 1.2f;

    /// <summary>
    /// Quantizes a line height to Blink's <c>LayoutUnit</c> grid (1/64px), flooring
    /// exactly as <c>LayoutUnit::FromFloatFloor</c> does. Browsers lay out line boxes
    /// on this grid, so the engine must use the same quantized value: the unquantized
    /// float is a fraction of a pixel taller per line, and over a chapter's hundreds of
    /// lines that drift tips the page count by one at a boundary. The multiply is done
    /// in double (as Blink does) so a value sitting on the 1/64px boundary is not
    /// pushed over it by float rounding.
    /// </summary>
    static float QuantizeLineHeight(float px) => (float)(Math.Truncate(px * 64.0) / 64.0);

    /// <summary>
    /// Quantizes a horizontal position to Blink's <c>LayoutUnit</c> grid (1/64px),
    /// ceiling exactly as <c>LayoutUnit::ToCeil</c> does. Blink decides a line break by
    /// comparing each glyph's START position (ceiled to this grid) against the available
    /// width, so a word stays on the line when its start is within the width even if its
    /// advance overflows it. The multiply is done in double (as Blink does) so a value
    /// sitting on the 1/64px boundary is not pushed over it by float rounding.
    /// </summary>
    static float QuantizeCeil(float px) => (float)(Math.Ceiling(px * 64.0) / 64.0);

    /// <summary>
    /// Measures text. When <paramref name="includeLineText"/> is false the measurer
    /// skips building the per-line strings (only line count and heights are produced),
    /// which callers that never read <see cref="TextMeasurementResult.Lines"/> — like
    /// the block layout engine, which only needs the count and heights — can use to
    /// avoid copying the whole text once per block.
    /// </summary>
    TextMeasurementResult Measure(string text, float fontSizePx, float maxWidthPx, float? lineHeightPx = null, float? letterSpacingPx = null, float? wordSpacingPx = null, float? firstLineWidthPx = null, FontStyle style = FontStyle.Regular, bool includeLineText = true);

    /// <summary>
    /// Measures text made of runs at DIFFERENT font sizes (browser model): word widths
    /// are measured at each run's own size and each line's height is the tallest inline
    /// box on that line. Line-height is passed in its original form: as a fixed length
    /// (<paramref name="lineHeightLengthPx"/>) or a unitless multiplier
    /// (<paramref name="lineHeightMultiplier"/>); when both are null the font's "normal"
    /// height (1.2 × size) is approximated per run.
    /// </summary>
    /// <summary>
    /// Measures multi-run text. <paramref name="includeLineText"/> has the same
    /// meaning as in <see cref="Measure"/>.
    /// </summary>
    TextMeasurementResult MeasureRuns(IReadOnlyList<TextRun> runs, float maxWidthPx, float? lineHeightLengthPx = null, float? lineHeightMultiplier = null, float? letterSpacingPx = null, float? wordSpacingPx = null, float? firstLineWidthPx = null, bool includeLineText = true);

    /// <summary>
    /// The font's ascent and descent (in px, both positive) at the given size: the
    /// distances from the baseline to the top and bottom of the font's em box. The
    /// layout engine uses them to compute the strut (the line box a line occupies)
    /// when sizing lines that contain inline images (baseline model).
    /// </summary>
    SKFontMetrics GetFontMetrics(float fontSizePx, FontStyle style);

    /// <summary>
    /// The full natural line height (ascent + descent + leading) for a font at the given size.
    /// Unlike <see cref="GetFontMetrics"/>, this includes the font's leading, matching how browsers
    /// compute the height of a line box (e.g. for <c>initial-letter</c> drop caps).
    /// </summary>
    float GetNaturalLineHeight(float fontSizePx, FontStyle style);

    /// <summary>
    /// Measures the width of a text run on a single line (no wrapping) in pixels.
    /// Used for table cell min/max-content sizing.
    /// </summary>
    float MeasureTextWidth(string text, float fontSizePx, FontStyle style);
}
