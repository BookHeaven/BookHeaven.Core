using System.Diagnostics;
using BookHeaven.Core.DOM.Engine.Models;

namespace BookHeaven.Core.DOM.Engine;

/// <summary>
/// Placement is COLUMN-FILL (fill the current page from top to bottom before
/// starting the next one) — the same strategy as the old line-based splitter, so
/// page counts stay equivalent, but the inputs are now explicit: each block carries
/// its OWN margins and paddings (exactly as in CSS) instead of being an implicit
/// gap, and this splitter performs the margin collapse (the same collapse the
/// engine used to measure the flow — see
/// <see cref="LayoutBlock.EffectiveTopMargin"/> / <see cref="LayoutBlock.EffectiveBottomMargin"/>).
///
/// For every block — the top-level blocks and, recursively, the children of
/// container blocks — in order:
///  1. PageBreakBefore (and the page is not empty) → new page.
///  2. Margin collapse (CSS): the gap before the block is the max of its EFFECTIVE
///     top margin — own top margin, plus the first child's margin when it collapsed
///     through a container without padding-top — and the previous sibling's
///     EFFECTIVE bottom margin. A container's first child contributes no top margin
///     of its own when the container has no padding-top (it already collapsed
///     through into the container's effective top margin). A page is one column of
///     a flex layout, so the collapsed margin is trimmed at the page boundary: only
///     the top margin of the first block of the FIRST page is consumed. At the top
///     of every other page the gap collapses to zero — neither the previous block's
///     bottom margin (clipped at the old page's edge) nor this block's top margin
///     (clipped at the new page's edge) is consumed. This also covers the case where
///     the gap does not fit below the previous sibling and forces a page break.
///  3. PaddingTop: paddings never collapse, so the padding is consumed on the page
///     even at the page top; if it does not fit below the previous content the block
///     (with its padding) starts on a new page.
///  4. Content:
///     - Text blocks split at LINE boundaries (whole lines only; a single line taller
///       than a page is pixel-sliced, a pathological case).
///     - Image blocks: if they fit on a fresh page they move whole; if they are
///       taller than a page they fill the remainder and are pixel-sliced.
///     - Container blocks behave like a browser: they are NOT one indivisible box.
///       Each child is placed individually in the shared page flow, with its own
///       margins and paddings (and recursively if the child is a container); the
///       margin collapse is applied per sibling scope. A page break inside a
///       container resumes its remaining children on the next page; the
///       container's own PaddingTop/Bottom bound the child flow. WITHOUT
///       padding-bottom the last child's bottom margin collapses through the
///       container: it becomes part of the container's effective bottom margin
///       (consumed by the next sibling's gap). WITH padding-bottom it stays
///       inside the content box and is consumed after the children, before the
///       container's PaddingBottom. A container with no measured children falls
///       back to whole-move / pixel-slice of its content height.
///  5. PaddingBottom: consumed after the last fragment; if it does not fit the page
///     remainder it is clipped at the page edge (a browser clips the overflow rather
///     than leaving the remainder blank).
///  6. PageBreakAfter (and the page is not empty) → new page.
///
/// AvoidBreakInside (CSS break-inside: avoid) is enforced between steps 2 and 3:
/// if the block's WHOLE flow (gap + paddings + content) does not fit the page
/// remainder but fits a fresh page, a new page is started BEFORE the gap is
/// consumed, so the normal placement below moves the block whole instead of
/// fragmenting it. A block taller than a page cannot honor the hint and is
/// broken as usual (the browser treats avoid as a weak suggestion that forced
/// breaks and page capacity override). For containers the "whole flow" height
/// is measured with a DRY RUN of the child placement — the exact same rules,
/// no slices recorded.
/// </summary>
public static class BlockPageSplitter
{
    public static BlockPageMap SplitToPages(IReadOnlyList<LayoutBlock> blocks, int pageHeight)
    {
        var pages = new List<BlockPageInfo>();
        if (pageHeight <= 0 || blocks.Count == 0)
        {
            return new BlockPageMap(pages);
        }

        // The engine emits blocks in document order; the splitter is the single
        // authority for placement and margin collapse, so no re-sort is needed.
        var indexed = blocks.Select((b, i) => (Block: b, Idx: i)).ToList();
        var context = new SplitContext(pageHeight);

        foreach (var entry in indexed)
        {
            context.PlaceBlock(entry.Block, entry.Idx, childIdx: -1, parentPaddingTop: 0f,
                ref context.PrevBottomMargin, ref context.PrevZeroHeightMargin);
        }

        // Only pages that actually received content are materialized (pageBlocks grows
        // lazily in RecordSlice). A trailing empty page is intentionally not emitted.
        for (var p = 0; p < context.PageBlocks.Count; p++)
        {
            pages.Add(new BlockPageInfo
            {
                Index = p,
                Start = p * pageHeight,
                Height = pageHeight,
                PageFlags = 0,
                Footnotes = context.PageFootnotes[p],
                Blocks = context.PageBlocks[p]
            });
        }

        // Content existed but nothing was placed
        // (e.g. zero-height blocks only) still yields one page.
        if (pages.Count == 0)
        {
            pages.Add(new BlockPageInfo
            {
                Index = 0,
                Start = 0,
                Height = pageHeight,
                PageFlags = 0
            });
        }

        return new BlockPageMap(pages);
    }

    /// <summary>
    /// Mutable state of a single split run: the lazily grown page lists and the
    /// running placement cursor (current page, used height, pending margins).
    /// A dry-run context applies the exact same placement rules but records no
    /// slices — it exists to MEASURE a flow's height (AvoidBreakInside).
    /// </summary>
    private sealed class SplitContext(int pageHeight, bool dryRun = false)
    {
        private int PageHeight { get; } = pageHeight;

        public bool DryRun { get; } = dryRun;

        public List<List<PageBlockSlice>> PageBlocks { get; } = [];

        public List<List<PageFootnoteInfo>> PageFootnotes { get; } = [];

        private int CurrentPage { get; set; }

        private float UsedOnPage { get; set; }

        private bool _paged;

        /// <summary>True once the dry run needed more than one page.</summary>
        public bool Paged => _paged;

        public float PrevBottomMargin;

        public float PrevZeroHeightMargin;

        public void PlaceBlock(LayoutBlock block, int blockIdx, int childIdx, float parentPaddingTop, ref float prevBottomMargin, ref float prevZeroHeightMargin)
        {
            var flags = block.Flags;

            // 1. Forced page break before the block.
            if ((flags & LayoutBlockFlags.PageBreakBefore) != 0 && UsedOnPage > 0) NewPage();

            // 2. Margin collapse (CSS): gap = max(this block's effective top margin,
            //    the previous sibling's effective bottom margin). A container's
            //    first child contributes no top margin when the container has no
            //    padding-top: its margin collapsed through the container and is
            //    already part of the container's effective top margin.
            //
            //    Page-boundary collapse: a page is one column of a flex layout, so
            //    the collapsed margin is trimmed at the page boundary. Only the top
            //    margin of the first block of the FIRST page is consumed; at the top
            //    of every other page the gap collapses to zero — neither the previous
            //    block's bottom margin (clipped at the old page's edge) nor this
            //    block's top margin (clipped at the new page's edge) is consumed. This
            //    also covers the case where the gap itself does not fit below the
            //    previous sibling and forces a page break.
            var topMargin = childIdx == 0 && parentPaddingTop <= 0f
                ? 0f
                : LayoutBlock.EffectiveTopMargin(block);
            // A zero-height spacer (no content, padding or border) collapses its top and
            // bottom margins through itself: the spacer and the following sibling share the
            // same point, and the spacer's margins are consumed as a single gap rather than
            // stacked on top of the next sibling's top margin.
            var isZeroHeightSpacer = block.Kind == BlockKind.Text && block is { LineCount: 0, ContentHeight: <= 0f, PaddingTop: <= 0f, PaddingBottom: <= 0f };
            var ownTopMargin = isZeroHeightSpacer ? Math.Max(block.MarginTop, block.MarginBottom) : topMargin;
            var gap = prevZeroHeightMargin > 0f
                ? Math.Max(0f, ownTopMargin - prevZeroHeightMargin)
                : Math.Max(prevBottomMargin, ownTopMargin);

            // 2b. AvoidBreakInside (CSS break-inside: avoid): if the whole block does
            //     not fit the page remainder but fits a fresh page, start the fresh
            //     page NOW so the placement below moves the block whole. On a fresh
            //     non-first page the gap is trimmed at the boundary (step 3), so it
            //     is excluded from the fresh-page check. A block taller than a page
            //     cannot honor the hint and falls through to the normal (breaking)
            //     placement. At the top of a page there is nothing to avoid.
            if ((flags & LayoutBlockFlags.AvoidBreakInside) != 0 && UsedOnPage > 0f)
            {
                var contentH = MeasureContentFlow(block, blockIdx);
                var fitsInPlace = UsedOnPage + gap + block.PaddingTop + contentH + block.PaddingBottom <= PageHeight;
                var fitsOnFresh = contentH <= PageHeight && block.PaddingTop + contentH + block.PaddingBottom <= PageHeight;
                if (!fitsInPlace && fitsOnFresh) NewPage();
            }

            if (gap > 0f)
            {
                if (UsedOnPage + gap > PageHeight && UsedOnPage > 0f)
                {
                    // The gap does not fit below the previous sibling: break the page.
                    // The new page is never the first, so the collapsed margin is
                    // trimmed at the boundary (gap -> 0).
                    NewPage();
                }
                else if (UsedOnPage == 0f && CurrentPage > 0)
                {
                    // Top of a non-first page (the previous content filled the page and
                    // started this one): trim the collapsed margin at the boundary.
                }
                else
                {
                    // Middle of a page, or the top of the first page: consume the gap.
                    UsedOnPage = Math.Min(UsedOnPage + gap, PageHeight);
                }
            }

            // 3. Padding top: paddings never collapse, so it is consumed even at the
            //    page top; if it does not fit below the previous content the block
            //    (with its padding) moves to a new page.
            if (block.PaddingTop > 0f)
            {
                if (UsedOnPage + block.PaddingTop > PageHeight && UsedOnPage > 0f) NewPage();
                UsedOnPage = Math.Min(UsedOnPage + block.PaddingTop, PageHeight);
            }

            switch (block)
            {
                // 4. Content. Footnotes are recorded by RecordSlice on the page where the
                //    first piece of the block is placed (old splitter collected them there too).
                case { Kind: BlockKind.Text, LineCount: > 0 }:
                    PlaceText(block, blockIdx, childIdx);
                    break;
                case { Kind: BlockKind.Container, Children.Count: > 0 }:
                    PlaceContainer(block, blockIdx);
                    break;
                default:
                    PlaceBox(block, blockIdx, childIdx);
                    break;
            }

            // 5. Padding bottom: consumed after the last fragment; if it does not fit
            //    the page remainder it is clipped at the page edge (a browser clips
            //    the overflow — leaving the remainder blank would waste the page).
            if (block.PaddingBottom > 0f)
            {
                UsedOnPage = Math.Min(UsedOnPage + block.PaddingBottom, PageHeight);
            }

            // 6. Forced page break after the block.
            if ((flags & LayoutBlockFlags.PageBreakAfter) != 0 && UsedOnPage > 0) NewPage();

            // The block's effective bottom margin feeds the next sibling's collapse
            // (a padding-less container includes its last child's bottom margin).
            if (isZeroHeightSpacer)
            {
                // The spacer's top and bottom margins collapse through it and are
                // consumed as a single gap; the next sibling's top margin collapses
                // with that gap rather than stacking on top of it.
                var consumedGap = Math.Max(Math.Max(prevZeroHeightMargin, prevBottomMargin), ownTopMargin);
                prevBottomMargin = 0f;
                prevZeroHeightMargin = consumedGap;
            }
            else
            {
                prevBottomMargin = LayoutBlock.EffectiveBottomMargin(block);
                prevZeroHeightMargin = 0f;
            }
        }

        private void PlaceContainer(LayoutBlock container, int blockIdx)
        {
            // Browser behaviour: a container is a sequence of block boxes, not one
            // indivisible box. Every child is placed individually in the shared page
            // flow with its own margins and paddings; a page break inside the
            // container simply resumes its remaining children on the next page.
            // The container's own PaddingTop/Bottom are consumed by PlaceBlock around
            // the child flow, and its propagated PageBreakBefore/After double-break
            // safely: the guard (usedOnPage > 0) skips a break on an empty page.
            var childPrevBottom = 0f;
            var childPrevZeroHeight = 0f;
            for (var ci = 0; ci < container.Children.Count; ci++)
            {
                PlaceBlock(container.Children[ci], blockIdx, ci, container.PaddingTop, ref childPrevBottom, ref childPrevZeroHeight);
            }

            // WITH padding-bottom the last child's effective bottom margin does NOT
            // collapse through the container: it stays inside the content box,
            // consumed after the children and before the container's PaddingBottom.
            // Like paddings it is never trimmed at the page top (it is clipped if
            // taller than the page remainder). WITHOUT padding-bottom it collapsed
            // through: it is part of the container's effective bottom margin,
            // consumed by the next sibling's gap (updated in PlaceBlock).
            ConsumeContainerTailMargin(container);
        }

        /// <summary>
        /// WITH padding-bottom the last child's effective bottom margin does NOT
        /// collapse through the container: it stays inside the content box,
        /// consumed after the children and before the container's PaddingBottom.
        /// Like paddings it is never trimmed at the page top (it is clipped if
        /// taller than the page remainder).
        /// </summary>
        private void ConsumeContainerTailMargin(LayoutBlock container)
        {
            if (!(container.PaddingBottom > 0f)) return;
            var lastMargin = LayoutBlock.EffectiveBottomMargin(container.Children[^1]);
            if (!(lastMargin > 0f)) return;
            if (UsedOnPage + lastMargin > PageHeight && UsedOnPage > 0f) NewPage();
            UsedOnPage = Math.Min(UsedOnPage + lastMargin, PageHeight);
        }

        /// <summary>
        /// The height the block's content occupies in the flow when placed whole:
        /// the exact text height (per-line sum, or lines × line height — the same
        /// formula PlaceText uses), the content height for boxes, or — for
        /// containers — a dry run of the child flow through the same placement
        /// rules (Infinity when the flow needs more than one page).
        /// </summary>
        private float MeasureContentFlow(LayoutBlock block, int blockIdx)
        {
            switch (block)
            {
                case { Kind: BlockKind.Text, LineCount: > 0 }:
                    var perLine = block.LineHeights;
                    if (perLine is { Length: > 0 } && perLine.Length == block.LineCount)
                    {
                        var sum = 0f;
                        foreach (var h in perLine) sum += h;
                        return sum;
                    }
                    return block.LineCount * block.LineHeightPx;
                case { Kind: BlockKind.Container, Children.Count: > 0 }:
                    return MeasureContainerFlow(block, blockIdx);
                default:
                    return block.ContentHeight;
            }
        }

        /// <summary>
        /// Dry run of the container's child flow on a fresh page: the exact
        /// placement rules (margin collapse, paddings, nested avoids) with no
        /// slices recorded. Returns the flow's total height, or Infinity when it
        /// does not fit a single page (the avoid hint cannot be honored).
        /// </summary>
        private float MeasureContainerFlow(LayoutBlock container, int blockIdx)
        {
            var scratch = new SplitContext(PageHeight, dryRun: true);
            var childPrevBottom = 0f;
            var childPrevZeroHeight = 0f;
            for (var ci = 0; ci < container.Children.Count; ci++)
                scratch.PlaceBlock(container.Children[ci], blockIdx, ci, container.PaddingTop, ref childPrevBottom, ref childPrevZeroHeight);
            scratch.ConsumeContainerTailMargin(container);
            return scratch.Paged ? float.PositiveInfinity : scratch.UsedOnPage;
        }

        private void RecordSlice(LayoutBlock block, int blockIdx, int childIdx, int lineStart, int lineCount, float y, float height, bool firstPiece)
        {
            if (DryRun) return;   // measurement only: the caller still updates UsedOnPage
            EnsurePage(CurrentPage);
            PageBlocks[CurrentPage].Add(new PageBlockSlice
            {
                BlockIndex = blockIdx,
                ChildIndex = childIdx,
                LineStart = lineStart,
                LineCount = lineCount,
                Lines = [.. block.Lines.Skip(lineStart).Take(lineCount)],
                Y = y,
                Height = height,
                Flags = block.Flags
            });
            if (firstPiece && block.Links.Count > 0)
            {
                PageFootnotes[CurrentPage].Add(new PageFootnoteInfo
                {
                    LineIndex = blockIdx,
                    Content = string.Join(",", block.Links)
                });
            }
        }

        private void EnsurePage(int idx)
        {
            if (DryRun) return;
            while (PageBlocks.Count <= idx)
            {
                PageBlocks.Add([]);
                PageFootnotes.Add([]);
            }
        }

        private void NewPage()
        {
            if (DryRun) _paged = true;
            CurrentPage++;
            UsedOnPage = 0f;
        }

        private void PlaceText(LayoutBlock block, int blockIdx, int childIdx)
        {
            var lineHeight = block.LineHeightPx;
            if (lineHeight <= 0f || lineHeight > PageHeight)
            {
                // A single line taller than a page is pathological: pixel-slice the content.
                PlaceBox(block, blockIdx, childIdx);
                return;
            }

            var remaining = block.LineCount;
            var placed = 0;
            var perLine = block.LineHeights;
            var havePerLine = perLine is { Length: > 0 } && perLine.Length == block.LineCount;
            while (remaining > 0)
            {
                var space = PageHeight - UsedOnPage;

                int fit;
                float sliceHeight;
                if (havePerLine)
                {
                    // Mixed-size lines: heights vary per line, so "how many fit" is a prefix sum.
                    fit = 0;
                    sliceHeight = 0f;
                    for (var li = placed; li < block.LineCount; li++)
                    {
                        var next = sliceHeight + perLine![li];
                        if (next > space) break;
                        sliceHeight = next;
                        fit++;
                    }
                }
                else
                {
                    // Uniform lines: the original arithmetic, kept bit-identical.
                    fit = (int)Math.Floor(space / lineHeight);
                    sliceHeight = fit * lineHeight;
                }

                if (fit <= 0)
                {
                    // No whole line fits here: the lines move whole to the next page.
                    NewPage();
                    continue;
                }

                if (fit >= remaining)
                {
                    // Uniform: the whole block height is remaining × lineHeight (original
                    // formula). Per-line: sliceHeight already accumulated the exact sum.
                    var fullHeight = havePerLine ? sliceHeight : remaining * lineHeight;
                    RecordSlice(block, blockIdx, childIdx, placed, remaining, UsedOnPage, fullHeight, firstPiece: placed == 0);
                    UsedOnPage += fullHeight;
                    remaining = 0;
                }
                else
                {
                    RecordSlice(block, blockIdx, childIdx, placed, fit, UsedOnPage, sliceHeight, firstPiece: placed == 0);
                    UsedOnPage += sliceHeight;
                    remaining -= fit;
                    placed += fit;
                }

                if (UsedOnPage >= PageHeight) NewPage();
            }
        }

        private void PlaceBox(LayoutBlock block, int blockIdx, int childIdx)
        {
            var h = block.ContentHeight;
            if (h <= 0f)
            {
                // Zero-height block: nothing is placed, no page is materialized (old behavior).
                return;
            }

            if (UsedOnPage + h <= PageHeight)
            {
                RecordSlice(block, blockIdx, childIdx, 0, 0, UsedOnPage, h, firstPiece: true);
                UsedOnPage += h;
                if (UsedOnPage >= PageHeight) NewPage();
                return;
            }

            if (h <= PageHeight)
            {
                // Does not fit the page remainder but fits a fresh page: move whole.
                NewPage();
                RecordSlice(block, blockIdx, childIdx, 0, 0, 0f, h, firstPiece: true);
                UsedOnPage += h;
                if (UsedOnPage >= PageHeight) NewPage();
                return;
            }

            // Taller than a page: fill the remainder of the current page, then slice.
            var remaining = h;
            var firstSlice = true;
            while (remaining > 0f)
            {
                var take = Math.Min(remaining, PageHeight - UsedOnPage);
                if (take > 0f)
                {
                    RecordSlice(block, blockIdx, childIdx, 0, 0, UsedOnPage, take, firstPiece: firstSlice);
                    UsedOnPage += take;
                    remaining -= take;
                    firstSlice = false;
                }
                if (UsedOnPage >= PageHeight) NewPage();
            }
        }
    }
}
