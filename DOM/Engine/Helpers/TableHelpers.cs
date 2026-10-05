using System.Globalization;
using System.Text;
using BookHeaven.Core.DOM.CSS;
using BookHeaven.Core.DOM.CSS.Models;
using BookHeaven.Core.DOM.HTML.Models;
using BookHeaven.Core.DOM.Services;
using BookHeaven.Core.DOM.Text.Abstractions;
using BookHeaven.Core.DOM.Text.Enums;

namespace BookHeaven.Core.DOM.Engine.Helpers;

/// <summary>
///     Faithful implementation of Chromium's table column-width algorithms plus
///     row-height computation, so the engine can predict a table's total height
///     without a browser.
///     <c>table-layout: auto</c> (the default) uses Blink's
///     <c>TableLayoutUtils::DistributeInlineSizeToComputedInlineSizeAuto</c>: the
///     distribution matches the browser's EMPIRICAL behaviour — in the percentage
///     case a percent column whose percentage size falls below its min-content is
///     clamped to min-content, and the growth is distributed to the remaining percent
///     columns in proportion to their (percentSize − min) increases. This was verified
///     against Playwright/Chromium (see <c>.browser-test/test_clamp.html</c>).
///     <c>table-layout: fixed</c> follows the CSS spec: the table's width is its
///     explicit <c>width</c> (or the full containing-block width when auto), column
///     widths come ONLY from the first row's cells and <c>&lt;col&gt;</c> elements
///     (content never influences them), percentages summing above 100% are scaled
///     down proportionally, and the remaining space is shared equally by the auto
///     columns.
/// </summary>
internal static class TableHelpers
{
    /// <summary>
    ///     Computes the table's column widths and total height. Returns null when the
    ///     element has no usable rows/columns.
    /// </summary>
    public static Result? Compute(
        Element table,
        float containingBlockContentWidth,
        Func<Element, CssStyle> getStyle,
        Func<Element, float> getFontSize,
        Func<Element, bool> isHidden,
        ITextMeasurer measurer,
        PageCalculatorOptions options,
        CoreOptions coreOptions,
        Func<Element, bool> isBlockDisplay,
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

                var cell = ParseCell(cellEl, r, col, getStyle, getFontSize, isHidden, isBlockDisplay, measurer, parser);
                cells.Add(cell);
                for (var dr = 0; dr < cell.RowSpan && r + dr < rowElements.Count; dr++)
                for (var dc = 0; dc < cell.ColSpan && col + dc < columnCount; dc++)
                    occupied[r + dr, col + dc] = true;
                col += cell.ColSpan;
            }
        }

        if (cells.Count == 0) return null;

        // --- Table box metrics (border-spacing, padding, border). ---
        var tableStyle = getStyle(table);
        var tableFontSize = getFontSize(table);
        var (borderSpacing, isCollapsed) = ParseBorderSpacing(tableStyle, parser);
        var tablePadL = ParseLength(tableStyle, "padding-left", containingBlockContentWidth, tableFontSize, parser) ??
                        0f;
        var tablePadR = ParseLength(tableStyle, "padding-right", containingBlockContentWidth, tableFontSize, parser) ??
                        0f;
        var tablePadT = ParseLength(tableStyle, "padding-top", containingBlockContentWidth, tableFontSize, parser) ??
                        0f;
        var tablePadB = ParseLength(tableStyle, "padding-bottom", containingBlockContentWidth, tableFontSize, parser) ??
                        0f;
        var tableBorderL =
            ParseLength(tableStyle, "border-left-width", containingBlockContentWidth, tableFontSize, parser) ?? 0f;
        var tableBorderR =
            ParseLength(tableStyle, "border-right-width", containingBlockContentWidth, tableFontSize, parser) ?? 0f;
        var tableBorderT =
            ParseLength(tableStyle, "border-top-width", containingBlockContentWidth, tableFontSize, parser) ?? 0f;
        var tableBorderB =
            ParseLength(tableStyle, "border-bottom-width", containingBlockContentWidth, tableFontSize, parser) ?? 0f;

        // --- Assignable width = used width − undistributable space. ---
        var undistributable = tableBorderL + tableBorderR + tablePadL + tablePadR
                              + (isCollapsed ? 0f : (columnCount + 1) * borderSpacing);

        // --- Column widths: the algorithm depends on table-layout. ---
        float[] columnWidths;
        if (IsFixedLayout(tableStyle))
        {
            // FIXED: the table's width is its explicit width (or the full
            // containing-block width); column widths come from the first row's
            // cells and <col> elements only — content never influences them.
            var usedWidth = ResolveFixedTableUsedWidth(tableStyle, containingBlockContentWidth, parser);
            columnWidths = DistributeFixed(
                table, Math.Max(0f, usedWidth - undistributable), cells, columnCount,
                getStyle, getFontSize, parser);
        }
        else
        {
            // AUTO: content-driven shrink-to-fit plus Chromium's distribution.
            // Build per-column constraints from single-span cells.
            var columns = new Column[columnCount];
            for (var c = 0; c < columnCount; c++) columns[c] = new Column();

            foreach (var cell in cells)
            {
                if (cell.ColSpan > 1) continue; // colspan handled below
                var col = columns[cell.Column];
                col.Min = Math.Max(col.Min, cell.OuterMin);
                col.Max = Math.Max(col.Max, cell.OuterMax);
                if (cell.Percent is { } p)
                {
                    col.Percent = col.Percent is { } cp ? Math.Max(cp, p) : p;
                }
                else if (cell.FixedWidthPx is { } f)
                {
                    col.IsFixed = true;
                    var outer = f + cell.PaddingLeft + cell.PaddingRight + cell.BorderLeft + cell.BorderRight;
                    col.Min = Math.Max(col.Min, outer);
                    col.Max = Math.Max(col.Max, outer);
                }
            }

            // Process colspan cells (percent-surplus, then min/max distribution).
            foreach (var cell in cells.Where(c => c.ColSpan > 1))
                DistributeColspanCell(cell, borderSpacing, columns);

            // Clamp cumulative percentages to 100% (auto tables).
            var totalPercent = 0f;
            foreach (var col in columns)
                if (col.Percent is { } p)
                {
                    var clamped = p + totalPercent > 1f ? Math.Max(0f, 1f - totalPercent) : p;
                    col.Percent = clamped;
                    totalPercent += clamped;
                }

            // Table used width (explicit, or shrink-to-fit with the percent-aware model).
            var usedWidth = ResolveTableUsedWidth(tableStyle, columns, containingBlockContentWidth, parser);
            columnWidths = DistributeAuto(Math.Max(0f, usedWidth - undistributable), columns, false);
        }

        // --- Row heights: wrap each cell's text to its (spanned) content width. ---
        var rowHeights = new float[rowElements.Count];
        for (var r = 0; r < rowElements.Count; r++)
        {
            var rowHeight = 0f;
            foreach (var cell in cells.Where(c => c.Row == r))
            {
                var contentWidth = CellContentWidth(cell, columnWidths, borderSpacing, isCollapsed);
                var cellContentHeight = MeasureCellContentHeight(
                    cell, contentWidth, measurer, options, coreOptions, parser,
                    getStyle, getFontSize, isBlockDisplay);
                var outerHeight = cellContentHeight + cell.PaddingTop + cell.PaddingBottom + cell.BorderTop +
                                  cell.BorderBottom;
                rowHeight = Math.Max(rowHeight, outerHeight);
            }

            rowHeights[r] = rowHeight;
        }

        // Fold the inter-row border-spacing into each row (trailing) so the row
        // heights sum exactly to the content height: the page splitter places the
        // rows individually, and the spacing must travel with its row.
        var rowSpacing = isCollapsed ? 0f : borderSpacing;
        for (var r = 0; r < rowHeights.Length - 1; r++)
            rowHeights[r] += rowSpacing;

        var contentHeight = rowHeights.Sum();

        return new Result
        {
            ContentHeight = contentHeight,
            RowHeights = rowHeights,
            TopEdge = tablePadT + tableBorderT,
            BottomEdge = tablePadB + tableBorderB,
            ColumnWidths = columnWidths
        };
    }

    // ------------------------------------------------------------------
    // Cell parsing
    // ------------------------------------------------------------------

    private static bool IsCellTag(Element el)
    {
        return el.TagName is "td" or "th";
    }

    /// <summary>
    ///     Collects the table's <c>&lt;tr&gt;</c> rows in document order. A row is either a
    ///     direct child of the table or a child of a <c>&lt;thead&gt;</c>/<c>&lt;tbody&gt;</c>/
    ///     <c>&lt;tfoot&gt;</c> section that is a direct child of the table.
    /// </summary>
    private static List<Element> CollectRows(Element table)
    {
        var rows = new List<Element>();
        foreach (var child in table.Children)
            if (child.TagName.Equals("tr", StringComparison.OrdinalIgnoreCase))
                rows.Add(child);
            else if (child.TagName is "thead" or "tbody" or "tfoot")
                foreach (var row in child.Children)
                    if (row.TagName.Equals("tr", StringComparison.OrdinalIgnoreCase))
                        rows.Add(row);

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
        Func<Element, bool> isHidden, Func<Element, bool> isBlockDisplay,
        ITextMeasurer measurer,
        CssLengthParser parser)
    {
        var style = getStyle(el);
        var fontSize = getFontSize(el);
        var styleEnum = CssValueParser.IsItalicStyle(style.GetPropertyValue("font-style"))
            ? FontStyle.Italic
            : FontStyle.Regular;

        var cell = new Cell
        {
            Row = row,
            Column = column,
            ColSpan = ParseSpan(el, "colspan"),
            RowSpan = ParseSpan(el, "rowspan"),
            Text = Element.GetVisibleTextContent(el, isHidden),
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
            if (len is > 0)
            {
                cell.LineHeightLengthPx = len;
            }
            else
            {
                var mult = parser.ParseUnitless(lh);
                if (mult is > 0) cell.LineHeightMultiplier = mult;
            }
        }

        // Width: percent or fixed length.
        var (percent, fixedPx) = ParseWidth(style, fontSize, parser);
        cell.Percent = percent;
        cell.FixedWidthPx = fixedPx;

        // Intrinsic content sizes: min = longest unbreakable word, max = single line.
        cell.ContentMin = MeasureMinContent(cell.Text, cell.FontSize, cell.Style, measurer);
        cell.ContentMax = MeasureMaxContent(cell.Text, cell.FontSize, cell.Style, measurer);

        // The cell's content is a block flow: each block-level child (and any
        // anonymous block of inline content) is measured with its OWN typography
        // and margins, matching the browser. See CollectBlocks.
        cell.Blocks = CollectBlocks(el, cell, getStyle, getFontSize, isHidden, isBlockDisplay, parser);
        return cell;
    }

    /// <summary>
    ///     Collects the cell's descendant image elements (skipping hidden subtrees)
    ///     into <paramref name="result" />.
    /// </summary>
    private static void CollectImages(Element el, Func<Element, bool> isHidden, List<Element> result)
    {
        if (isHidden(el)) return;
        if (ImageHelpers.IsImageElement(el))
        {
            result.Add(el);
            return;
        }

        foreach (var child in el.Children)
            CollectImages(child, isHidden, result);
    }

    /// <summary>
    ///     Splits a cell's content into its block-flow boxes. Each block-display child becomes
    ///     its own block (with its own typography and margins); runs of inline content that are
    ///     not inside a block child are gathered into an anonymous block inheriting the cell's
    ///     typography. A cell with no content yields a single empty anonymous block.
    /// </summary>
    private static List<CellBlock> CollectBlocks(
        Element cellEl, Cell cell,
        Func<Element, CssStyle> getStyle, Func<Element, float> getFontSize,
        Func<Element, bool> isHidden, Func<Element, bool> isBlockDisplay, CssLengthParser parser)
    {
        var blocks = new List<CellBlock>();
        var anonText = new StringBuilder();
        var anonImages = new List<Element>();
        var hasAnon = false;

        // Flush the accumulated inline run (text and/or inline images) as an anonymous
        // block. A run with only images (no text) still forms a block, so a cell whose
        // sole content is a picture is measured correctly.
        void FlushAnon()
        {
            if (!hasAnon) return;
            blocks.Add(MakeAnonBlock(anonText.ToString().Trim(), anonImages, cell));
            anonText.Clear();
            anonImages.Clear();
            hasAnon = false;
        }

        // Iterate ChildNodes (not Children): a cell's text lives in text nodes, which
        // Children (elements only) would skip.
        foreach (var node in cellEl.ChildNodes)
            if (node is Element el)
            {
                if (isHidden(el)) continue;
                if (isBlockDisplay(el))
                {
                    FlushAnon();
                    blocks.Add(MakeBlock(el, getStyle, getFontSize, isHidden, parser));
                }
                else
                {
                    var text = Element.GetVisibleTextContent(el, isHidden);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        if (anonText.Length > 0) anonText.Append(' ');
                        anonText.Append(text);
                    }

                    CollectImages(el, isHidden, anonImages);
                    if (!string.IsNullOrWhiteSpace(text) || anonImages.Count > 0) hasAnon = true;
                }
            }
            else if (node is HTML.Models.Text text)
            {
                // Whitespace-only text (e.g. the newlines between block children) is
                // ignored by CSS: it forms no anonymous block, so adjacent blocks'
                // margins collapse exactly as in the browser.
                if (string.IsNullOrWhiteSpace(text.Value)) continue;
                if (anonText.Length > 0) anonText.Append(' ');
                anonText.Append(text.Value);
                hasAnon = true;
            }

        FlushAnon();

        if (blocks.Count == 0)
            blocks.Add(MakeAnonBlock(string.Empty, [], cell));

        return blocks;
    }

    /// <summary>Builds a block from a block-display element, capturing its own typography and box.</summary>
    private static CellBlock MakeBlock(
        Element el, Func<Element, CssStyle> getStyle, Func<Element, float> getFontSize,
        Func<Element, bool> isHidden, CssLengthParser parser)
    {
        var style = getStyle(el);
        var fontSize = getFontSize(el);
        var block = new CellBlock
        {
            Text = Element.GetVisibleTextContent(el, isHidden),
            FontSize = fontSize,
            Style = CssValueParser.IsItalicStyle(style.GetPropertyValue("font-style"))
                ? FontStyle.Italic
                : FontStyle.Regular,
            MarginTop = ParseLength(style, "margin-top", 0f, fontSize, parser) ?? 0f,
            MarginBottom = ParseLength(style, "margin-bottom", 0f, fontSize, parser) ?? 0f,
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

        // Line-height form (length vs unitless multiplier); inherits the resolved value
        // when the element declares none, exactly like the browser.
        var lh = style.GetPropertyValue("line-height");
        if (!string.IsNullOrWhiteSpace(lh) && !lh.Trim().Equals("normal", StringComparison.OrdinalIgnoreCase))
        {
            var len = ParseLength(style, "line-height", 0f, fontSize, parser);
            if (len is > 0)
            {
                block.LineHeightLengthPx = len;
            }
            else
            {
                var mult = parser.ParseUnitless(lh);
                if (mult is > 0) block.LineHeightMultiplier = mult;
            }
        }

        CollectImages(el, isHidden, block.Images);
        return block;
    }

    /// <summary>Builds an anonymous block from inline content, inheriting the cell's typography.</summary>
    private static CellBlock MakeAnonBlock(string text, List<Element> images, Cell cell)
    {
        return new CellBlock
        {
            Text = text,
            FontSize = cell.FontSize,
            Style = cell.Style,
            LineHeightLengthPx = cell.LineHeightLengthPx,
            LineHeightMultiplier = cell.LineHeightMultiplier,
            // Copy: the caller reuses/clears its working list after this returns.
            Images = [.. images]
        };
    }

    // A <br> is serialized as U+FFFC (see DomModels.AppendDescendantText) and is a forced
    // break, exactly like the main text path (TextRunWrapper / HarfBuzzTextMeasurer).
    // Min-content splits on it (and on spaces); max-content measures each <br>-separated
    // segment as its own line and takes the widest.
    private static float MeasureMinContent(string text, float fontSize, FontStyle style, ITextMeasurer measurer)
    {
        if (string.IsNullOrEmpty(text)) return 0f;
        var max = 0f;
        foreach (var word in text.Split([' ', '\uFFFC'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
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
                if (columns[i].Percent is { } p)
                {
                    spannedPct += p;
                }
                else
                {
                    nonPctMax += columns[i].Max;
                    nonPctCount++;
                }

            var surplus = cp - spannedPct;
            if (surplus > 0f && nonPctCount > 0)
                for (var i = start; i < start + span; i++)
                {
                    if (columns[i].Percent is not null) continue;
                    var share = nonPctMax > 0f ? surplus * columns[i].Max / nonPctMax : surplus / nonPctCount;
                    columns[i].Percent = share;
                }
        }

        // Min distribution: only ever INCREASES a column's min.
        var spanCols = columns[start..(start + span)];
        var minSizes = DistributeAuto(colspanMin, spanCols, true);
        for (var i = 0; i < span; i++)
            columns[start + i].Min = Math.Max(columns[start + i].Min, minSizes[i]);

        // Max distribution: only ever INCREASES a column's max (and keeps max >= min).
        var maxSizes = DistributeAuto(colspanMax, spanCols, cell.FixedWidthPx is not null);
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

        foreach (var col in cols)
        {
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
                foreach (var col in cols)
                {
                    if (col.Percent is not { } p) continue;
                    var inc = p * target - col.Min;
                    if (inc > 0f)
                    {
                        positiveIncrease += inc;
                        positiveCount++;
                    }
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
                        var delta = specIncrease > 0f
                            ? distributable * (inc / specIncrease)
                            : distributable / Math.Max(1, fixedCount);
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
                        var delta = maxIncrease > 0f
                            ? distributable * (inc / maxIncrease)
                            : distributable / Math.Max(1, autoCount);
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
                            var delta = totalAutoMax > 0f
                                ? distributable * (col.Max / totalAutoMax)
                                : distributable / autoCount;
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
                            var delta = totalFixedMax > 0f
                                ? distributable * (col.Max / totalFixedMax)
                                : distributable / fixedCount;
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
                        var delta = totalPercent > 0f
                            ? distributable * (p / totalPercent)
                            : distributable / Math.Max(1, percentCount);
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

    private static float ResolveTableUsedWidth(CssStyle style, Column[] columns, float available,
        CssLengthParser parser)
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
            // Standard shrink-to-fit.
            return Math.Min(Math.Max(totalMin, available), totalMax);

        // Percent-aware shrink-to-fit: the width must be large enough that every
        // percent column reaches its min-content, and at least the max-content.
        var minForPercents = 0f;
        var nonPercentMin = 0f;
        foreach (var col in columns)
            if (col.Percent is { } p and > 0)
                minForPercents = Math.Max(minForPercents, col.Min / p);
            else
                nonPercentMin += col.Min;
        var wMin = nonPercentMin + minForPercents;
        var wMax = totalMax;

        if (wMin >= available) return available;
        return Math.Max(wMin, wMax);
    }

    // ------------------------------------------------------------------
    // table-layout: fixed
    // ------------------------------------------------------------------

    private static bool IsFixedLayout(CssStyle style)
    {
        return string.Equals(style.GetPropertyValue("table-layout").Trim(), "fixed",
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Fixed-layout table width: an explicit <c>width</c> (clamped to the available
    ///     width), or the FULL available width when <c>width</c> is auto — content never
    ///     shrinks a fixed table, so it can never exceed the containing block.
    /// </summary>
    private static float ResolveFixedTableUsedWidth(CssStyle style, float available, CssLengthParser parser)
    {
        var width = style.GetPropertyValue("width");
        if (!string.IsNullOrWhiteSpace(width) && !width.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            var px = ParseLength(style, "width", available, 0f, parser);
            if (px is > 0) return Math.Min(px.Value, available);
        }

        return available;
    }

    /// <summary>
    ///     CSS <c>table-layout: fixed</c> column-width algorithm: column widths are
    ///     determined by the <c>&lt;col&gt;</c> elements and the FIRST row's cells only —
    ///     content never influences them. Percent widths resolve against the assignable
    ///     width; percentages summing above 100% are scaled down proportionally,
    ///     specified widths exceeding the assignable width are scaled down
    ///     proportionally, and the remaining space is shared EQUALLY by the auto
    ///     columns.
    /// </summary>
    private static float[] DistributeFixed(
        Element table, float assignable, List<Cell> cells, int columnCount,
        Func<Element, CssStyle> getStyle, Func<Element, float> getFontSize,
        CssLengthParser parser)
    {
        var percents = new float[columnCount];
        var fixedWidths = new float[columnCount];
        var isAuto = new bool[columnCount];
        for (var c = 0; c < columnCount; c++) isAuto[c] = true;

        // <col> elements (direct children, or inside <colgroup>) set column widths first.
        var colIndex = 0;
        foreach (var colEl in CollectColElements(table))
        {
            var span = ParseSpan(colEl, "span");
            var (percent, fixedPx) = ParseWidth(getStyle(colEl), getFontSize(colEl), parser);
            if (percent is not null || fixedPx is not null)
            {
                var start = Math.Min(colIndex, columnCount - 1);
                ApplyFixedSpan(start, Math.Min(span, columnCount - start), percent, fixedPx, percents, fixedWidths,
                    isAuto);
            }

            colIndex += span;
        }

        // The first row's cells set (or override) the widths of the columns they span.
        foreach (var cell in cells.Where(c => c.Row == 0))
        {
            var start = cell.Column;
            var span = Math.Min(cell.ColSpan, columnCount - start);
            if (span <= 0) continue;
            if (cell.Percent is { } p)
            {
                ApplyFixedSpan(start, span, p, null, percents, fixedWidths, isAuto);
            }
            else if (cell.FixedWidthPx is { } f)
            {
                var outer = f + cell.PaddingLeft + cell.PaddingRight + cell.BorderLeft + cell.BorderRight;
                ApplyFixedSpan(start, span, null, outer, percents, fixedWidths, isAuto);
            }
        }

        // Percentages summing above 100% are scaled down proportionally (spec).
        var totalPercent = 0f;
        for (var c = 0; c < columnCount; c++) totalPercent += percents[c];
        if (totalPercent > 1f)
            for (var c = 0; c < columnCount; c++)
                percents[c] /= totalPercent;

        var computed = new float[columnCount];
        var specifiedTotal = 0f;
        for (var c = 0; c < columnCount; c++)
        {
            if (isAuto[c]) continue;
            computed[c] = percents[c] * assignable + fixedWidths[c];
            specifiedTotal += computed[c];
        }

        // Specified widths exceeding the assignable width are scaled down proportionally.
        if (specifiedTotal > assignable && specifiedTotal > 0f)
        {
            var scale = assignable / specifiedTotal;
            for (var c = 0; c < columnCount; c++)
                if (!isAuto[c])
                    computed[c] *= scale;
            specifiedTotal = assignable;
        }

        // The remaining space is shared equally by the auto columns.
        var autoCount = 0;
        for (var c = 0; c < columnCount; c++)
            if (isAuto[c])
                autoCount++;
        var autoShare = autoCount > 0 ? (assignable - specifiedTotal) / autoCount : 0f;
        for (var c = 0; c < columnCount; c++)
            if (isAuto[c])
                computed[c] = autoShare;

        return computed;
    }

    /// <summary>
    ///     Applies a specified width to the columns a <c>&lt;col&gt;</c> element or
    ///     first-row cell spans. A single column is set directly (the later source
    ///     wins); a span shares the percent proportionally to the spanned columns'
    ///     existing percents (equally when none), and the fixed width proportionally
    ///     to their existing fixed widths (equally when none).
    /// </summary>
    private static void ApplyFixedSpan(
        int start, int span, float? percent, float? fixedPx,
        float[] percents, float[] fixedWidths, bool[] isAuto)
    {
        if (span == 1)
        {
            if (percent is { } singlePercent)
            {
                percents[start] = singlePercent;
                fixedWidths[start] = 0f;
            }
            else if (fixedPx is { } singleFixed)
            {
                fixedWidths[start] = singleFixed;
                percents[start] = 0f;
            }

            isAuto[start] = false;
            return;
        }

        if (percent is { } p)
        {
            var existing = 0f;
            for (var i = start; i < start + span; i++) existing += percents[i];
            for (var i = start; i < start + span; i++)
            {
                percents[i] += existing > 0f ? p * percents[i] / existing : p / span;
                isAuto[i] = false;
            }
        }

        if (fixedPx is { } f)
        {
            var existing = 0f;
            for (var i = start; i < start + span; i++) existing += fixedWidths[i];
            for (var i = start; i < start + span; i++)
            {
                fixedWidths[i] += existing > 0f ? f * fixedWidths[i] / existing : f / span;
                isAuto[i] = false;
            }
        }
    }

    /// <summary>
    ///     Collects the table's <c>&lt;col&gt;</c> elements in document order: direct
    ///     children of the table, plus children of a <c>&lt;colgroup&gt;</c> that is a
    ///     direct child of the table.
    /// </summary>
    private static List<Element> CollectColElements(Element table)
    {
        var cols = new List<Element>();
        foreach (var child in table.Children)
            if (child.TagName.Equals("col", StringComparison.OrdinalIgnoreCase))
                cols.Add(child);
            else if (child.TagName.Equals("colgroup", StringComparison.OrdinalIgnoreCase))
                foreach (var grandchild in child.Children)
                    if (grandchild.TagName.Equals("col", StringComparison.OrdinalIgnoreCase))
                        cols.Add(grandchild);

        return cols;
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

    /// <summary>
    ///     Measures the cell's content-box height: the wrapped text plus any images.
    ///     Block-display images stack in the cell's block flow; an inline image
    ///     stretches the line it sits on (or forms a line of its own when the cell
    ///     has no text), matching the browser's replaced-element model.
    /// </summary>
    private static float MeasureCellContentHeight(
        Cell cell, float contentWidth, ITextMeasurer measurer,
        PageCalculatorOptions options, CoreOptions coreOptions,
        CssLengthParser parser,
        Func<Element, CssStyle> getStyle, Func<Element, float> getFontSize,
        Func<Element, bool> isBlockDisplay)
    {
        // The cell's content is a block flow. Each block is measured with its OWN
        // typography (a nested <p> uses its own line-height, not the cell's); adjacent
        // blocks' vertical margins collapse (max), while the first block's top margin
        // and the last block's bottom margin are kept in full (a table cell establishes
        // a block formatting context, so they never collapse with the cell's padding).
        var blocks = cell.Blocks;
        var total = 0f;
        foreach (var block in blocks)
        {
            var lineHeightPx = block.LineHeightLengthPx
                               ?? (block.LineHeightMultiplier ?? ITextMeasurer.NormalLineHeightMultiplier) *
                               block.FontSize;
            var textWidth = Math.Max(0f,
                contentWidth - block.PaddingLeft - block.PaddingRight - block.BorderLeft - block.BorderRight);
            var textHeight = string.IsNullOrEmpty(block.Text)
                ? 0f
                : measurer.Measure(block.Text, block.FontSize, textWidth, lineHeightPx, style: block.Style,
                    includeLineText: false).TotalHeightPx;

            var blockImagesHeight = 0f;
            var inlineImageHeight = 0f;
            foreach (var img in block.Images)
            {
                var (_, imgHeight) = ImageHelpers.MeasureImageSize(
                    img, getStyle(img), ParseLen, options, coreOptions,
                    textWidth, textWidth, 0f, getFontSize(img));
                if (isBlockDisplay(img)) blockImagesHeight += imgHeight;
                else inlineImageHeight = Math.Max(inlineImageHeight, imgHeight);
            }

            var hasText = !string.IsNullOrEmpty(block.Text);
            var inlineContribution = inlineImageHeight <= 0 ? 0f
                : hasText ? Math.Max(0f, inlineImageHeight - lineHeightPx) // stretches its text line
                : Math.Max(inlineImageHeight, lineHeightPx); // forms its own line

            total += block.BorderTop + block.PaddingTop + textHeight + inlineContribution + blockImagesHeight
                     + block.PaddingBottom + block.BorderBottom;
        }

        if (blocks.Count > 0)
        {
            total += blocks[0].MarginTop;
            total += blocks[^1].MarginBottom;
            for (var i = 0; i + 1 < blocks.Count; i++)
                total += Math.Max(blocks[i].MarginBottom, blocks[i + 1].MarginTop);
        }

        return total;

        float? ParseLen(string? value, float reference, float? emReference)
        {
            return parser.ParseLengthToPx(value, reference, emReference);
        }
    }

    // ------------------------------------------------------------------
    // Small parsing helpers (self-contained; use the public length parser).
    // ------------------------------------------------------------------

    private static float? ParseLength(CssStyle style, string name, float reference, float emReference,
        CssLengthParser parser)
    {
        return parser.ParseLengthToPx(style.GetPropertyValue(name), reference, emReference);
    }

    /// <summary>
    ///     Parses a <c>width</c> declaration into a percent (0..1) and/or a fixed pixel
    ///     width; <c>auto</c> (or an unparseable value) yields both null.
    /// </summary>
    private static (float? Percent, float? FixedPx) ParseWidth(CssStyle style, float fontSize, CssLengthParser parser)
    {
        var width = style.GetPropertyValue("width");
        if (string.IsNullOrWhiteSpace(width)) return (null, null);
        var t = width.Trim();
        if (t.EndsWith('%'))
        {
            if (float.TryParse(t[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
                return (pct / 100f, null);
        }
        else if (!t.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            var px = ParseLength(style, "width", 0f, fontSize, parser);
            if (px is > 0) return (null, px.Value);
        }

        return (null, null);
    }

    private static (float Spacing, bool Collapsed) ParseBorderSpacing(CssStyle style, CssLengthParser parser)
    {
        var collapse = style.GetPropertyValue("border-collapse");
        var isCollapsed = collapse.Trim().Equals("collapse", StringComparison.OrdinalIgnoreCase);
        var raw = style.GetPropertyValue("border-spacing");
        var spacing = 2f; // UA default
        if (!string.IsNullOrWhiteSpace(raw))
        {
            var first = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
            var px = parser.ParseLengthToPx(first, 0f);
            if (px is > 0) spacing = px.Value;
        }

        return (spacing, isCollapsed);
    }

    /// <summary>The computed geometry of a table.</summary>
    public sealed class Result
    {
        /// <summary>Content-box height (rows + inter-row border-spacing).</summary>
        public required float ContentHeight { get; init; }

        /// <summary>
        ///     Per-row outer heights (cell content + cell padding + cell border), with the
        ///     trailing inter-row border-spacing folded into every row but the last — so
        ///     the rows sum exactly to <see cref="ContentHeight" /> and the page splitter
        ///     can place them individually without losing the spacing.
        /// </summary>
        public required float[] RowHeights { get; init; }

        /// <summary>Space above the content box: padding-top + border-top.</summary>
        public required float TopEdge { get; init; }

        /// <summary>Space below the content box: padding-bottom + border-bottom.</summary>
        public required float BottomEdge { get; init; }

        /// <summary>Computed column widths (border-box of each column).</summary>
        public required float[] ColumnWidths { get; init; }
    }

    /// <summary>A parsed table cell with its intrinsic content constraints.</summary>
    private sealed class Cell
    {
        public List<CellBlock> Blocks = [];
        public float BorderLeft, BorderRight, BorderTop, BorderBottom;
        public int ColSpan = 1;
        public int Column;
        public float ContentMax; // single-line width

        public float ContentMin; // longest unbreakable word
        public float? FixedWidthPx; // from width: Npx (a constrained column)

        public float FontSize;
        public float? LineHeightLengthPx;
        public float? LineHeightMultiplier;

        public float PaddingLeft, PaddingRight, PaddingTop, PaddingBottom;

        public float? Percent; // 0..1, from width: X%
        public int Row;
        public int RowSpan = 1;
        public FontStyle Style;
        public string Text = string.Empty;

        public float OuterMin => ContentMin + PaddingLeft + PaddingRight + BorderLeft + BorderRight;
        public float OuterMax => ContentMax + PaddingLeft + PaddingRight + BorderLeft + BorderRight;
        public float OuterHeight => 0f; // filled during row-height pass
    }

    /// <summary>
    ///     A block-level box in a cell's block flow: a <c>&lt;p&gt;</c>/<c>&lt;div&gt;</c> (or any
    ///     block-display element) or an anonymous block of inline content. Each carries its OWN
    ///     typography (font-size, line-height, style) and its vertical margins/padding/borders,
    ///     because the browser lays out a cell's content as a block flow — the cell's own
    ///     line-height does NOT apply to text nested inside a child block.
    /// </summary>
    private sealed class CellBlock
    {
        public float BorderLeft, BorderRight, BorderTop, BorderBottom;
        public float FontSize;

        public List<Element> Images = [];
        public float? LineHeightLengthPx;
        public float? LineHeightMultiplier;

        public float MarginTop, MarginBottom;
        public float PaddingLeft, PaddingRight, PaddingTop, PaddingBottom;
        public FontStyle Style;
        public string Text = string.Empty;
    }

    /// <summary>A column's accumulated inline-size constraints.</summary>
    private sealed class Column
    {
        public bool IsFixed;
        public float Max;
        public float Min;
        public float? Percent;
    }
}