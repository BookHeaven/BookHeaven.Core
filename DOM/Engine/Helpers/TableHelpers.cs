using System.Globalization;
using BookHeaven.Core.DOM.CSS;
using BookHeaven.Core.DOM.CSS.Models;
using BookHeaven.Core.DOM.HTML.Models;
using BookHeaven.Core.DOM.Services;
using BookHeaven.Core.DOM.Text.Abstractions;
using BookHeaven.Core.DOM.Text.Enums;

namespace BookHeaven.Core.DOM.Engine.Helpers;

/// <summary>
/// Faithful implementation of Chromium's <c>table-layout: auto</c> column-width
/// distribution (Blink <c>TableLayoutUtils::DistributeInlineSizeToComputedInlineSizeAuto</c>)
/// plus row-height computation, so the engine can predict a table's total height
/// without a browser.
///
/// The distribution matches the browser's EMPIRICAL behaviour: in the percentage
/// case a percent column whose percentage size falls below its min-content is
/// clamped to min-content, and the growth is distributed to the remaining percent
/// columns in proportion to their (percentSize − min) increases. This was verified
/// against Playwright/Chromium (see <c>.browser-test/test_clamp.html</c>).
/// </summary>
internal static class TableHelpers
{
    /// <summary>The computed geometry of a table.</summary>
    public sealed class Result
    {
        /// <summary>Content-box height (rows + inter-row border-spacing).</summary>
        public required float ContentHeight { get; init; }

        /// <summary>Border-box height (content + vertical padding + vertical border).</summary>
        public required float BorderBoxHeight { get; init; }

        /// <summary>Computed column widths (border-box of each column).</summary>
        public required float[] ColumnWidths { get; init; }
    }

    /// <summary>A parsed table cell with its intrinsic content constraints.</summary>
    private sealed class Cell
    {
        public int Row;
        public int Column;
        public int ColSpan = 1;
        public int RowSpan = 1;
        public string Text = string.Empty;

        public float? Percent;        // 0..1, from width: X%
        public float? FixedWidthPx;   // from width: Npx (a constrained column)

        public float PaddingLeft, PaddingRight, PaddingTop, PaddingBottom;
        public float BorderLeft, BorderRight, BorderTop, BorderBottom;

        public float FontSize;
        public FontStyle Style;
        public float? LineHeightLengthPx;
        public float? LineHeightMultiplier;

        public float ContentMin; // longest unbreakable word
        public float ContentMax; // single-line width

        public float OuterMin => ContentMin + PaddingLeft + PaddingRight + BorderLeft + BorderRight;
        public float OuterMax => ContentMax + PaddingLeft + PaddingRight + BorderLeft + BorderRight;
        public float OuterHeight => 0f; // filled during row-height pass
    }

    /// <summary>A column's accumulated inline-size constraints.</summary>
    private sealed class Column
    {
        public float Min;
        public float Max;
        public float? Percent;
        public bool IsFixed;
    }

    /// <summary>
    /// Computes the table's column widths and total height. Returns null when the
    /// element has no usable rows/columns.
    /// </summary>
    public static Result? Compute(
        Element table,
        float containingBlockContentWidth,
        Func<Element, CssStyle> getStyle,
        Func<Element, float> getFontSize,
        ITextMeasurer measurer,
        PageCalculatorOptions options,
        RenderContext context,
        CssLengthParser parser)
    {
        // --- Collect rows and cells into a grid (colspan + rowspan aware). ---
        // Rows may be direct children of the table OR wrapped in <thead>/<tbody>/<tfoot>;
        // collect them in document order.
        var rowElements = CollectRows(table);
        if (rowElements.Count == 0) return null;

        var columnCount = 0;
        foreach (var rowEl in rowElements)
        {
            var sum = 0;
            foreach (var cellEl in rowEl.Children.Where(IsCellTag))
                sum += ParseSpan(cellEl, "colspan");
            columnCount = Math.Max(columnCount, sum);
        }
        if (columnCount == 0) return null;

        var cells = new List<Cell>();
        var occupied = new bool[rowElements.Count, columnCount];
        for (var r = 0; r < rowElements.Count; r++)
        {
            var col = 0;
            foreach (var cellEl in rowElements[r].Children.Where(IsCellTag))
            {
                while (col < columnCount && occupied[r, col]) col++;
                if (col >= columnCount) break;

                var cell = ParseCell(cellEl, r, col, getStyle, getFontSize, measurer, options, context, parser);
                cells.Add(cell);
                for (var dr = 0; dr < cell.RowSpan && r + dr < rowElements.Count; dr++)
                    for (var dc = 0; dc < cell.ColSpan && col + dc < columnCount; dc++)
                        occupied[r + dr, col + dc] = true;
                col += cell.ColSpan;
            }
        }
        if (cells.Count == 0) return null;

        // --- Build per-column constraints from single-span cells. ---
        var columns = new Column[columnCount];
        for (var c = 0; c < columnCount; c++) columns[c] = new Column();

        foreach (var cell in cells)
        {
            if (cell.ColSpan > 1) continue; // colspan handled below
            var col = columns[cell.Column];
            col.Min = Math.Max(col.Min, cell.OuterMin);
            col.Max = Math.Max(col.Max, cell.OuterMax);
            if (cell.Percent is { } p)
                col.Percent = col.Percent is { } cp ? Math.Max(cp, p) : p;
            else if (cell.FixedWidthPx is { } f)
            {
                col.IsFixed = true;
                var outer = f + cell.PaddingLeft + cell.PaddingRight + cell.BorderLeft + cell.BorderRight;
                col.Min = Math.Max(col.Min, outer);
                col.Max = Math.Max(col.Max, outer);
            }
        }

        // --- Table box metrics (border-spacing, padding, border). ---
        var tableStyle = getStyle(table);
        var tableFontSize = getFontSize(table);
        var (borderSpacing, isCollapsed) = ParseBorderSpacing(tableStyle, context, parser);
        var tablePadL = ParseLength(tableStyle, "padding-left", containingBlockContentWidth, tableFontSize, parser) ?? 0f;
        var tablePadR = ParseLength(tableStyle, "padding-right", containingBlockContentWidth, tableFontSize, parser) ?? 0f;
        var tablePadT = ParseLength(tableStyle, "padding-top", containingBlockContentWidth, tableFontSize, parser) ?? 0f;
        var tablePadB = ParseLength(tableStyle, "padding-bottom", containingBlockContentWidth, tableFontSize, parser) ?? 0f;
        var tableBorderL = ParseLength(tableStyle, "border-left-width", containingBlockContentWidth, tableFontSize, parser) ?? 0f;
        var tableBorderR = ParseLength(tableStyle, "border-right-width", containingBlockContentWidth, tableFontSize, parser) ?? 0f;
        var tableBorderT = ParseLength(tableStyle, "border-top-width", containingBlockContentWidth, tableFontSize, parser) ?? 0f;
        var tableBorderB = ParseLength(tableStyle, "border-bottom-width", containingBlockContentWidth, tableFontSize, parser) ?? 0f;

        // --- Process colspan cells (percent-surplus, then min/max distribution). ---
        foreach (var cell in cells.Where(c => c.ColSpan > 1))
            DistributeColspanCell(cell, borderSpacing, columns);

        // --- Clamp cumulative percentages to 100% (auto tables). ---
        var totalPercent = 0f;
        foreach (var col in columns)
        {
            if (col.Percent is { } p)
            {
                var clamped = p + totalPercent > 1f ? Math.Max(0f, 1f - totalPercent) : p;
                col.Percent = clamped;
                totalPercent += clamped;
            }
        }

        // --- Table used width (explicit, or shrink-to-fit with the percent-aware model). ---
        var usedWidth = ResolveTableUsedWidth(tableStyle, columns.ToArray(), containingBlockContentWidth, context, parser);

        // --- Assignable width = used width − undistributable space. ---
        var undistributable = (tableBorderL + tableBorderR + tablePadL + tablePadR)
            + (isCollapsed ? 0f : (columnCount + 1) * borderSpacing);
        var assignable = Math.Max(0f, usedWidth - undistributable);

        // --- Distribute the assignable width across columns. ---
        var columnWidths = DistributeAuto(assignable, columns.ToArray(), treatTargetAsConstrained: false);

        // --- Row heights: wrap each cell's text to its (spanned) content width. ---
        var rowHeights = new float[rowElements.Count];
        for (var r = 0; r < rowElements.Count; r++)
        {
            var rowHeight = 0f;
            foreach (var cell in cells.Where(c => c.Row == r))
            {
                var contentWidth = CellContentWidth(cell, columnWidths, borderSpacing, isCollapsed);
                var cellContentHeight = MeasureCellContentHeight(cell, contentWidth, measurer);
                var outerHeight = cellContentHeight + cell.PaddingTop + cell.PaddingBottom + cell.BorderTop + cell.BorderBottom;
                rowHeight = Math.Max(rowHeight, outerHeight);
            }
            rowHeights[r] = rowHeight;
        }

        var contentHeight = rowHeights.Sum() + Math.Max(0, rowElements.Count - 1) * (isCollapsed ? 0f : borderSpacing);
        var borderBoxHeight = contentHeight + tablePadT + tablePadB + tableBorderT + tableBorderB;

        return new Result
        {
            ContentHeight = contentHeight,
            BorderBoxHeight = borderBoxHeight,
            ColumnWidths = columnWidths
        };
    }

    // ------------------------------------------------------------------
    // Cell parsing
    // ------------------------------------------------------------------

    private static bool IsCellTag(Element el) =>
        el.TagName is "td" or "th";

    /// <summary>
    /// Collects the table's <c>&lt;tr&gt;</c> rows in document order. A row is either a
    /// direct child of the table or a child of a <c>&lt;thead&gt;</c>/<c>&lt;tbody&gt;</c>/
    /// <c>&lt;tfoot&gt;</c> section that is a direct child of the table.
    /// </summary>
    private static List<Element> CollectRows(Element table)
    {
        var rows = new List<Element>();
        foreach (var child in table.Children)
        {
            if (child.TagName.Equals("tr", StringComparison.OrdinalIgnoreCase))
            {
                rows.Add(child);
            }
            else if (child.TagName is "thead" or "tbody" or "tfoot")
            {
                foreach (var row in child.Children)
                    if (row.TagName.Equals("tr", StringComparison.OrdinalIgnoreCase))
                        rows.Add(row);
            }
        }
        return rows;
    }

    private static int ParseSpan(Element el, string attr)
    {
        var raw = el.GetAttribute(attr);
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : 1;
    }

    private static Cell ParseCell(
        Element el, int row, int column,
        Func<Element, CssStyle> getStyle, Func<Element, float> getFontSize,
        ITextMeasurer measurer, PageCalculatorOptions options, RenderContext context,
        CssLengthParser parser)
    {
        var style = getStyle(el);
        var fontSize = getFontSize(el);
        var styleEnum = CssValueParser.IsItalicStyle(style.GetPropertyValue("font-style")) ? FontStyle.Italic : FontStyle.Regular;

        var cell = new Cell
        {
            Row = row,
            Column = column,
            ColSpan = ParseSpan(el, "colspan"),
            RowSpan = ParseSpan(el, "rowspan"),
            Text = el.TrimmedTextContent,
            FontSize = fontSize,
            Style = styleEnum,
            PaddingLeft = ParseLength(style, "padding-left", 0f, fontSize, parser) ?? 0f,
            PaddingRight = ParseLength(style, "padding-right", 0f, fontSize, parser) ?? 0f,
            PaddingTop = ParseLength(style, "padding-top", 0f, fontSize, parser) ?? 0f,
            PaddingBottom = ParseLength(style, "padding-bottom", 0f, fontSize, parser) ?? 0f,
            BorderLeft = ParseLength(style, "border-left-width", 0f, fontSize, parser) ?? 0f,
            BorderRight = ParseLength(style, "border-right-width", 0f, fontSize, parser) ?? 0f,
            BorderTop = ParseLength(style, "border-top-width", 0f, fontSize, parser) ?? 0f,
            BorderBottom = ParseLength(style, "border-bottom-width", 0f, fontSize, parser) ?? 0f,
            LineHeightLengthPx = null,
            LineHeightMultiplier = null
        };

        // Line-height form (length vs unitless multiplier), browser model.
        var lh = style.GetPropertyValue("line-height");
        if (!string.IsNullOrWhiteSpace(lh) && !lh.Trim().Equals("normal", StringComparison.OrdinalIgnoreCase))
        {
            var len = ParseLength(style, "line-height", 0f, fontSize, parser);
            if (len is > 0) cell.LineHeightLengthPx = len;
            else
            {
                var mult = parser.ParseUnitless(lh);
                if (mult is > 0) cell.LineHeightMultiplier = mult;
            }
        }

        // Width: percent or fixed length.
        var width = style.GetPropertyValue("width");
        if (!string.IsNullOrWhiteSpace(width))
        {
            var t = width.Trim();
            if (t.EndsWith('%'))
            {
                if (float.TryParse(t[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
                    cell.Percent = pct / 100f;
            }
            else if (!t.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                var px = ParseLength(style, "width", 0f, fontSize, parser);
                if (px is > 0) cell.FixedWidthPx = px;
            }
        }

        // Intrinsic content sizes: min = longest unbreakable word, max = single line.
        cell.ContentMin = MeasureMinContent(cell.Text, cell.FontSize, cell.Style, measurer);
        cell.ContentMax = MeasureMaxContent(cell.Text, cell.FontSize, cell.Style, measurer);
        return cell;
    }

    // A <br> is serialized as U+FFFC (see DomModels.AppendDescendantText) and is a forced
    // break, exactly like the main text path (TextRunWrapper / HarfBuzzTextMeasurer).
    // Min-content splits on it (and on spaces); max-content measures each <br>-separated
    // segment as its own line and takes the widest.
    private static float MeasureMinContent(string text, float fontSize, FontStyle style, ITextMeasurer measurer)
    {
        if (string.IsNullOrEmpty(text)) return 0f;
        var max = 0f;
        foreach (var word in text.Split([' ', '\uFFFC'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            max = Math.Max(max, measurer.MeasureTextWidth(word, fontSize, style));
        return max;
    }

    private static float MeasureMaxContent(string text, float fontSize, FontStyle style, ITextMeasurer measurer)
    {
        if (string.IsNullOrEmpty(text)) return 0f;
        var max = 0f;
        foreach (var segment in text.Split('\uFFFC', StringSplitOptions.RemoveEmptyEntries))
            max = Math.Max(max, measurer.MeasureTextWidth(segment, fontSize, style));
        return max;
    }

    // ------------------------------------------------------------------
    // Colspan processing (DistributeColspanCellToColumnsAuto)
    // ------------------------------------------------------------------

    private static void DistributeColspanCell(Cell cell, float borderSpacing, Column[] columns)
    {
        var start = cell.Column;
        var span = Math.Min(cell.ColSpan, columns.Length - start);
        if (span <= 1) return;

        var innerSpacing = (span - 1) * borderSpacing;
        var colspanMin = Math.Max(0f, cell.OuterMin - innerSpacing);
        var colspanMax = Math.Max(0f, cell.OuterMax - innerSpacing);

        // Percent surplus → non-percent spanned columns (proportional to max).
        if (cell.Percent is { } cp)
        {
            var spannedPct = 0f;
            var nonPctMax = 0f;
            var nonPctCount = 0;
            for (var i = start; i < start + span; i++)
            {
                if (columns[i].Percent is { } p) spannedPct += p;
                else { nonPctMax += columns[i].Max; nonPctCount++; }
            }
            var surplus = cp - spannedPct;
            if (surplus > 0f && nonPctCount > 0)
            {
                for (var i = start; i < start + span; i++)
                {
                    if (columns[i].Percent is not null) continue;
                    var share = nonPctMax > 0f ? surplus * columns[i].Max / nonPctMax : surplus / nonPctCount;
                    columns[i].Percent = share;
                }
            }
        }

        // Min distribution: only ever INCREASES a column's min.
        var spanCols = columns[start..(start + span)];
        var minSizes = DistributeAuto(colspanMin, spanCols, treatTargetAsConstrained: true);
        for (var i = 0; i < span; i++)
            columns[start + i].Min = Math.Max(columns[start + i].Min, minSizes[i]);

        // Max distribution: only ever INCREASES a column's max (and keeps max >= min).
        var maxSizes = DistributeAuto(colspanMax, spanCols, treatTargetAsConstrained: cell.FixedWidthPx is not null);
        for (var i = 0; i < span; i++)
            columns[start + i].Max = Math.Max(Math.Max(columns[start + i].Min, columns[start + i].Max), maxSizes[i]);
    }

    // ------------------------------------------------------------------
    // The core distribution (DistributeInlineSizeToComputedInlineSizeAuto)
    // ------------------------------------------------------------------

    private static float[] DistributeAuto(float target, Column[] cols, bool treatTargetAsConstrained)
    {
        var percentCount = 0;
        var fixedCount = 0;
        var autoCount = 0;
        var totalPercent = 0f;
        var totalAutoMax = 0f;
        var totalFixedMax = 0f;

        var minGuess = 0f;
        var pctGuess = 0f;
        var specGuess = 0f;
        var maxGuess = 0f;
        var specIncrease = 0f;
        var maxIncrease = 0f;

        for (var i = 0; i < cols.Length; i++)
        {
            var col = cols[i];
            if (col.Percent is { } p)
            {
                percentCount++;
                totalPercent += p;
                var pctSize = p * target;
                minGuess += col.Min;
                pctGuess += pctSize;
                specGuess += pctSize;
                maxGuess += pctSize;
            }
            else if (col.IsFixed)
            {
                fixedCount++;
                totalFixedMax += col.Max;
                minGuess += col.Min;
                pctGuess += col.Min;
                specGuess += col.Max;
                maxGuess += col.Max;
                specIncrease += col.Max - col.Min;
            }
            else
            {
                autoCount++;
                totalAutoMax += col.Max;
                minGuess += col.Min;
                pctGuess += col.Min;
                specGuess += col.Min;
                maxGuess += col.Max;
                maxIncrease += col.Max - col.Min;
            }
        }

        var computed = new float[cols.Length];
        target = Math.Max(target, minGuess);

        var starting = 4; // kAboveMax
        if (minGuess >= target) starting = 0;
        else if (pctGuess >= target) starting = 1;
        else if (specGuess >= target) starting = 2;
        else if (maxGuess >= target) starting = 3;

        switch (starting)
        {
            case 0: // kMinGuess
                for (var i = 0; i < cols.Length; i++) computed[i] = cols[i].Min;
                break;

            case 1: // kPercentageGuess — empirical clamp + proportional redistribute.
            {
                var distributable = target - minGuess;
                var positiveIncrease = 0f;
                var positiveCount = 0;
                for (var i = 0; i < cols.Length; i++)
                {
                    if (cols[i].Percent is not { } p) continue;
                    var inc = p * target - cols[i].Min;
                    if (inc > 0f) { positiveIncrease += inc; positiveCount++; }
                }
                for (var i = 0; i < cols.Length; i++)
                {
                    var col = cols[i];
                    if (col.Percent is { } p)
                    {
                        var inc = p * target - col.Min;
                        if (inc <= 0f)
                        {
                            computed[i] = col.Min; // clamped to min-content
                        }
                        else
                        {
                            var delta = positiveIncrease > 0f
                                ? distributable * (inc / positiveIncrease)
                                : distributable / Math.Max(1, positiveCount);
                            computed[i] = col.Min + delta;
                        }
                    }
                    else
                    {
                        computed[i] = col.Min; // auto/fixed get their min
                    }
                }
            }
                break;

            case 2: // kSpecifiedGuess
            {
                var distributable = target - pctGuess;
                var remaining = distributable;
                var last = -1;
                for (var i = 0; i < cols.Length; i++)
                {
                    var col = cols[i];
                    if (col.Percent is { } p)
                    {
                        computed[i] = p * target;
                    }
                    else if (col.IsFixed)
                    {
                        last = i;
                        var inc = col.Max - col.Min;
                        var delta = specIncrease > 0f ? distributable * (inc / specIncrease) : distributable / Math.Max(1, fixedCount);
                        remaining -= delta;
                        computed[i] = col.Min + delta;
                    }
                    else
                    {
                        computed[i] = col.Min;
                    }
                }
                if (last >= 0) computed[last] += remaining;
            }
                break;

            case 3: // kMaxGuess
            {
                var distributable = target - specGuess;
                var exactMatch = Math.Abs(target - maxGuess) < 0.001f;
                var remaining = exactMatch ? 0f : distributable;
                var last = -1;
                for (var i = 0; i < cols.Length; i++)
                {
                    var col = cols[i];
                    if (col.Percent is { } p)
                    {
                        computed[i] = p * target;
                    }
                    else if (col.IsFixed || exactMatch)
                    {
                        computed[i] = col.Max;
                    }
                    else
                    {
                        last = i;
                        var inc = col.Max - col.Min;
                        var delta = maxIncrease > 0f ? distributable * (inc / maxIncrease) : distributable / Math.Max(1, autoCount);
                        remaining -= delta;
                        computed[i] = col.Min + delta;
                    }
                }
                if (last >= 0) computed[last] += remaining;
            }
                break;

            case 4: // kAboveMax
            {
                var distributable = target - maxGuess;
                if (autoCount > 0)
                {
                    var remaining = distributable;
                    var last = -1;
                    for (var i = 0; i < cols.Length; i++)
                    {
                        var col = cols[i];
                        if (col.Percent is { } p)
                        {
                            computed[i] = p * target;
                        }
                        else if (col.IsFixed)
                        {
                            computed[i] = col.Max;
                        }
                        else
                        {
                            last = i;
                            var delta = totalAutoMax > 0f ? distributable * (col.Max / totalAutoMax) : distributable / autoCount;
                            remaining -= delta;
                            computed[i] = col.Max + delta;
                        }
                    }
                    if (last >= 0) computed[last] += remaining;
                }
                else if (fixedCount > 0 && treatTargetAsConstrained)
                {
                    var remaining = distributable;
                    var last = -1;
                    for (var i = 0; i < cols.Length; i++)
                    {
                        var col = cols[i];
                        if (col.Percent is { } p)
                        {
                            computed[i] = p * target;
                        }
                        else if (col.IsFixed)
                        {
                            last = i;
                            var delta = totalFixedMax > 0f ? distributable * (col.Max / totalFixedMax) : distributable / fixedCount;
                            remaining -= delta;
                            computed[i] = col.Max + delta;
                        }
                    }
                    if (last >= 0) computed[last] += remaining;
                }
                else
                {
                    var remaining = distributable;
                    var last = -1;
                    for (var i = 0; i < cols.Length; i++)
                    {
                        var col = cols[i];
                        if (col.Percent is not { } p) continue;
                        last = i;
                        var delta = totalPercent > 0f ? distributable * (p / totalPercent) : distributable / Math.Max(1, percentCount);
                        remaining -= delta;
                        computed[i] = p * target + delta;
                    }
                    if (last >= 0) computed[last] += remaining;
                }
            }
                break;
        }

        return computed;
    }

    // ------------------------------------------------------------------
    // Table used width
    // ------------------------------------------------------------------

    private static float ResolveTableUsedWidth(CssStyle style, Column[] columns, float available, RenderContext context, CssLengthParser parser)
    {
        var width = style.GetPropertyValue("width");
        if (!string.IsNullOrWhiteSpace(width) && !width.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            var px = ParseLength(style, "width", available, 0f, parser);
            if (px is > 0) return Math.Min(px.Value, available);
        }

        var hasPercent = columns.Any(c => c.Percent is not null);
        var totalMin = columns.Sum(c => c.Min);
        var totalMax = columns.Sum(c => c.Max);

        if (!hasPercent)
        {
            // Standard shrink-to-fit.
            return Math.Min(Math.Max(totalMin, available), totalMax);
        }

        // Percent-aware shrink-to-fit: the width must be large enough that every
        // percent column reaches its min-content, and at least the max-content.
        var minForPercents = 0f;
        var nonPercentMin = 0f;
        foreach (var col in columns)
        {
            if (col.Percent is { } p and > 0)
                minForPercents = Math.Max(minForPercents, col.Min / p);
            else
                nonPercentMin += col.Min;
        }
        var wMin = nonPercentMin + minForPercents;
        var wMax = totalMax;

        if (wMin >= available) return available;
        return Math.Max(wMin, wMax);
    }

    // ------------------------------------------------------------------
    // Row-height helpers
    // ------------------------------------------------------------------

    private static float CellContentWidth(Cell cell, float[] columnWidths, float borderSpacing, bool isCollapsed)
    {
        var span = Math.Min(cell.ColSpan, columnWidths.Length - cell.Column);
        var width = 0f;
        for (var i = 0; i < span; i++)
        {
            if (i > 0 && !isCollapsed) width += borderSpacing;
            width += columnWidths[cell.Column + i];
        }
        width -= cell.PaddingLeft + cell.PaddingRight + cell.BorderLeft + cell.BorderRight;
        return Math.Max(0f, width);
    }

    private static float MeasureCellContentHeight(Cell cell, float contentWidth, ITextMeasurer measurer)
    {
        if (string.IsNullOrEmpty(cell.Text)) return 0f;
        var lineHeightPx = cell.LineHeightLengthPx ?? (cell.LineHeightMultiplier ?? ITextMeasurer.NormalLineHeightMultiplier) * cell.FontSize;
        var result = measurer.Measure(cell.Text, cell.FontSize, contentWidth, lineHeightPx: lineHeightPx, style: cell.Style, includeLineText: false);
        return result.TotalHeightPx;
    }

    // ------------------------------------------------------------------
    // Small parsing helpers (self-contained; use the public length parser).
    // ------------------------------------------------------------------

    private static float? ParseLength(CssStyle style, string name, float reference, float emReference, CssLengthParser parser)
        => parser.ParseLengthToPx(style.GetPropertyValue(name), new RenderContext(reference, reference, emReference), reference, emReference);

    private static (float Spacing, bool Collapsed) ParseBorderSpacing(CssStyle style, RenderContext context, CssLengthParser parser)
    {
        var collapse = style.GetPropertyValue("border-collapse");
        var isCollapsed = collapse.Trim().Equals("collapse", StringComparison.OrdinalIgnoreCase);
        var raw = style.GetPropertyValue("border-spacing");
        var spacing = 2f; // UA default
        if (!string.IsNullOrWhiteSpace(raw))
        {
            var first = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            var px = parser.ParseLengthToPx(first, context, 0f);
            if (px is > 0) spacing = px.Value;
        }
        return (spacing, isCollapsed);
    }
}
