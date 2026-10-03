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
///     - Text blocks split at LINE boundaries (whole lines only; a single line
///       taller than a page is kept in place at the top of a page — or moved to
///       a fresh page mid-page — and the overflow is clipped, as in a browser).
///     - Image blocks: if they fit on a fresh page they move whole; if they are
///       taller than a page they are kept in place (or moved to a fresh page
///       mid-page) and the overflow is clipped.
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
///       back to whole-move / clip of its content height.
///  5. PaddingBottom: consumed after the last fragment. Paddings are never truncated
///     at a break (unlike margins): if it does not fit the page remainder it overflows
///     the page edge (clipped visually) and the excess carries into the next page,
///     pushing the following block down — as in a browser.
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
///
/// AvoidBreakAfter (CSS break-after: avoid) is enforced right after: if the
/// block fits the page remainder but the NEXT block does not fit after it (so
/// this block would be the last on the page), a new page is started BEFORE the
/// gap is consumed, so the block moves whole and the break lands after the next
/// block instead of right after this one.
/// </summary>
public static class PageSplitter
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
        var context = new SplitContext(pageHeight);

        for (var i = 0; i < blocks.Count; i++)
        {
            // Top-level blocks are direct children of the body. The body is a flex ITEM
            // of the html (the html element is the flex container), and flex items do
            // NOT collapse their margins with their parent. So the first block's top
            // margin is CONSUMED on the first page (childIdx -1 keeps it); it does not
            // leak out past the page's top edge. Subsequent blocks keep their top margin
            // and collapse normally with the previous block's bottom margin; on following
            // pages the gap is trimmed at the page boundary (handled by PlaceBlock).
            context.PlaceBlock(blocks[i], i, childIdx: -1, trimTopMargin: false,
                nextBlock: i + 1 < blocks.Count ? blocks[i + 1] : null,
                ref context.PrevBottomMargin, ref context.PrevZeroHeightMargin);
        }

        // Only pages that actually received content are materialized (PageBlocks grows
        // lazily in RecordSlice). A trailing empty page is intentionally not emitted,
        // but a page that received carried content (e.g. a padding overflow) is not
        // empty — materialize it.
        context.MaterializeCurrentPageIfUsed();
        for (var p = 0; p < context.PageBlocks.Count; p++)
        {
            pages.Add(new BlockPageInfo
            {
                Blocks = context.PageBlocks[p]
            });
        }

        // Content existed but nothing was placed
        // (e.g. zero-height blocks only) still yields one page.
        if (pages.Count == 0)
        {
            pages.Add(new BlockPageInfo());
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

        private bool DryRun { get; } = dryRun;

        public List<List<PageBlockSlice>> PageBlocks { get; } = [];

        private int CurrentPage { get; set; }

        private float UsedOnPage { get; set; }

        /// <summary>True once the dry run needed more than one page.</summary>
        private bool Paged { get; set; }

        /// <summary>
        /// True when the block currently being placed forced a page break BEFORE its
        /// content was placed (forced break, avoid hint, margin gap or padding that
        /// did not fit the page remainder). The browser creates a fresh column for
        /// such a block even when it is zero-height, so a zero-height block with
        /// margins must materialize the page it forced (see PlaceBox). Reset at the
        /// top of every PlaceBlock; post-placement breaks (line/box overflow,
        /// PageBreakAfter) do not set it.
        /// </summary>
        private bool ForcedPageBreak;

        public float PrevBottomMargin;

        public float PrevZeroHeightMargin;

        /// <summary>
        /// A child's top margin is trimmed (folded into the container's effective top
        /// margin) when the container has no padding-top and every preceding sibling is
        /// a zero-height spacer OR a float — the CSS collapse then ran through those to
        /// this child, so its top margin is already part of the container's effective
        /// top margin and must not be added again as a gap. A float is out of flow and
        /// does not block the collapse run (it is not a zero-height spacer, but it also
        /// does not stop the run). Only the first child is trimmed when the leading
        /// siblings are content blocks (the common case).
        /// </summary>
        private static bool ShouldTrimTopMargin(LayoutBlock container, int childIdx)
            => LayoutBlock.ShouldTrimTopMargin(container.Children, childIdx, container.PaddingTop);

        public void PlaceBlock(LayoutBlock block, int blockIdx, int childIdx, bool trimTopMargin, LayoutBlock? nextBlock, ref float prevBottomMargin, ref float prevZeroHeightMargin)
        {
            var flags = block.Flags;
            ForcedPageBreak = false;

            // 1. Forced page break before the block.
            if ((flags & LayoutBlockFlags.PageBreakBefore) != 0 && UsedOnPage > 0)
            {
                ForcedPageBreak = true;
                NewPage();
            }

            // 2. Margin collapse (CSS): gap = max(this block's effective top margin,
            //    the previous sibling's effective bottom margin). A block's top margin
            //    is trimmed (folded into the parent's effective top margin) when it
            //    collapsed through the parent: the parent has no padding-top and every
            //    preceding sibling is a zero-height spacer, so the CSS collapse ran
            //    through those spacers to this block and its margin is already part of
            //    the parent's effective top margin (see ShouldTrimTopMargin).
            //
            //    Page-boundary collapse: a page is one column of a flex layout, so
            //    the collapsed margin is trimmed at the page boundary. Only the top
            //    margin of the first block of the FIRST page is consumed; at the top
            //    of every other page the gap collapses to zero — neither the previous
            //    block's bottom margin (clipped at the old page's edge) nor this
            //    block's top margin (clipped at the new page's edge) is consumed. This
            //    also covers the case where the gap itself does not fit below the
            //    previous sibling and forces a page break.
            // A float's top margin is never trimmed: a float is out of flow, so its
            // margin does not collapse with the parent's (it is not folded into the
            // parent's effective top margin) and is always consumed as a gap.
            var topMargin = (trimTopMargin && !block.Floats) ? 0f : LayoutBlock.EffectiveTopMargin(block);
            // A zero-height spacer (no content, padding or border) collapses its top and
            // bottom margins through itself: the spacer and the following sibling share the
            // same point, and the spacer's margins are consumed as a single gap rather than
            // stacked on top of the next sibling's top margin.
            var isZeroHeightSpacer = LayoutBlock.IsZeroHeightSpacer(block);
            // A spacer whose top margin was already trimmed (first child of a
            // padding-less container) must not re-introduce it via the combined
            // spacer margin — that would consume the collapsed margin twice.
            var ownTopMargin = isZeroHeightSpacer && topMargin > 0f
                ? Math.Max(block.MarginTop, block.MarginBottom)
                : topMargin;
            var gap = prevZeroHeightMargin > 0f
                ? Math.Max(0f, ownTopMargin - prevZeroHeightMargin)
                : Math.Max(prevBottomMargin, ownTopMargin);

            // 2b. AvoidBreakInside / monolithic: if the whole block does not fit the
            //     page remainder but fits a fresh page, start the fresh page NOW so the
            //     placement below moves the block whole. This applies to blocks with the
            //     AvoidBreakInside flag and to monolithic blocks (single-line text) that
            //     cannot be broken internally, so their padding is never left behind on
            //     the previous page. On a fresh non-first page the gap is trimmed at the
            //     boundary (step 3), so it is excluded from the fresh-page check. A block
            //     taller than a page cannot honor the hint and falls through to the normal
            //     (breaking) placement. At the top of a page there is nothing to avoid.
            var isMonolithic = block is { Kind: BlockKind.Text, LineCount: 1 };
            if (((flags & LayoutBlockFlags.AvoidBreakInside) != 0 || isMonolithic) && UsedOnPage > 0f)
            {
                var contentH = MeasureContentFlow(block, blockIdx);
                var fitsInPlace = UsedOnPage + gap + block.PaddingTop + contentH + block.PaddingBottom <= PageHeight;
                var fitsOnFresh = contentH <= PageHeight && block.PaddingTop + contentH + block.PaddingBottom <= PageHeight;
                if (!fitsInPlace && fitsOnFresh)
                {
                    ForcedPageBreak = true;
                    NewPage();
                }
            }

            // 2c. AvoidBreakAfter (CSS break-after: avoid): a page break must not land
            //     right after this block. If the block fits the page remainder but the
            //     NEXT block cannot even START after it (its first line would go to the
            //     next page), start a fresh page NOW so the block moves whole and the
            //     break lands after the next block instead. If the next block would
            //     break INSIDE (first line fits, rest flows on) there is no break after
            //     this block, so nothing is avoided. At the top of a page there is
            //     nothing to avoid (moving the first block would leave an empty page).
            if ((flags & LayoutBlockFlags.AvoidBreakAfter) != 0 && nextBlock is not null && UsedOnPage > 0f)
            {
                var contentH = MeasureContentFlow(block, blockIdx);
                var fitsInPlace = UsedOnPage + gap + block.PaddingTop + contentH + block.PaddingBottom <= PageHeight;
                if (fitsInPlace)
                {
                    var usedAfter = UsedOnPage + gap + block.PaddingTop + contentH + block.PaddingBottom;
                    if (!CanStartOnPage(nextBlock, usedAfter, block))
                    {
                        ForcedPageBreak = true;
                        NewPage();
                    }
                }
            }

            if (gap > 0f)
            {
                if (UsedOnPage + gap > PageHeight && UsedOnPage > 0f)
                {
                    // The gap does not fit below the previous sibling: break the page.
                    // The new page is never the first, so the collapsed margin is
                    // trimmed at the boundary (gap -> 0).
                    ForcedPageBreak = true;
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
                if (UsedOnPage + block.PaddingTop > PageHeight && UsedOnPage > 0f)
                {
                    ForcedPageBreak = true;
                    NewPage();
                }
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

            // 5. Padding bottom: consumed after the last fragment. Paddings are never
            //    truncated at a break (unlike margins, css-break-3 §5.2): if it does not
            //    fit the page remainder it overflows the page edge (clipped visually) and
            //    the excess carries into the next page, pushing the following block down.
            if (block.PaddingBottom > 0f)
            {
                UsedOnPage += block.PaddingBottom;
                if (UsedOnPage > PageHeight)
                {
                    var overflow = UsedOnPage - PageHeight;
                    NewPage();
                    UsedOnPage = overflow;
                }
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
            // If the whole container fits on the page, place it as a single box: the
            // content height already reflects any explicit height / max-height, so a
            // fixed-height container is clipped to its box and its children need not
            // be traversed. The container's PaddingTop was consumed by PlaceBlock
            // before this call and its PaddingBottom is consumed after it, so only the
            // content box is placed here.
            if (container.ContentHeight <= PageHeight - UsedOnPage)
            {
                RecordSlice(blockIdx, childIdx: -1, 0, container.LineCount, UsedOnPage, container.ContentHeight);
                UsedOnPage += container.ContentHeight;
                if (UsedOnPage >= PageHeight) NewPage();
                return;
            }

            // Does not fit the page remainder: a container is a sequence of block
            // boxes, not one indivisible box. Every child is placed individually in
            // the shared page flow with its own margins and paddings; a page break
            // inside the container simply resumes its remaining children on the next
            // page. The container's own PaddingTop/Bottom are consumed by PlaceBlock
            // around the child flow, and its propagated PageBreakBefore/After
            // double-break safely: the guard (usedOnPage > 0) skips a break on an
            // empty page.
            var flowStart = UsedOnPage;
            var flowStartPage = CurrentPage;
            var childPrevBottom = 0f;
            var childPrevZeroHeight = 0f;
            for (var ci = 0; ci < container.Children.Count; ci++)
            {
                PlaceBlock(container.Children[ci], blockIdx, ci, ShouldTrimTopMargin(container, ci),
                    nextBlock: ci + 1 < container.Children.Count ? container.Children[ci + 1] : null,
                    ref childPrevBottom, ref childPrevZeroHeight);
            }

            // A FIXED-height container's box is authoritative (height / min-height /
            // max-height): it occupies exactly ContentHeight in the flow, whatever
            // the children consume. When the children underflow the box (e.g. a
            // min-height taller than the content) the box's remaining height is
            // consumed after them — and, like paddings, it is never truncated at a
            // break: if it does not fit the page remainder it overflows the page
            // edge and the excess carries into the next page, pushing the following
            // block down (browser behaviour). A box taller than a page therefore
            // pushes the following content to the next page even when every child
            // fit the first one.
            if ((container.Flags & LayoutBlockFlags.FixedHeight) != 0)
            {
                // Total flow distance the children consumed, counting the full
                // height of every page they broke across (UsedOnPage alone only
                // measures the last page).
                var consumed = (CurrentPage - flowStartPage) * PageHeight + (UsedOnPage - flowStart);
                var deficit = container.ContentHeight - consumed;
                if (deficit > 0f)
                {
                    if (UsedOnPage + deficit > PageHeight && UsedOnPage > 0f) NewPage();
                    UsedOnPage += deficit;
                    if (UsedOnPage > PageHeight)
                    {
                        var overflow = UsedOnPage - PageHeight;
                        NewPage();
                        UsedOnPage = overflow;
                    }
                }
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
        /// rules (Infinity when the flow needs more than one page). A FIXED-height
        /// container is the exception: its content box is authoritative (the engine
        /// already applied height / max-height) and the children's overflow is
        /// clipped at the box, so the flow height is exactly ContentHeight and no
        /// dry run is needed.
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
                // NOTE: a flags-enum pattern (Flags: not FixedHeight) would be an
                // EQUALITY test, so a block carrying FixedHeight together with any
                // other flag would still dry-run; use an explicit bit test.
                case { Kind: BlockKind.Container, Children.Count: > 0 }
                    when (block.Flags & LayoutBlockFlags.FixedHeight) == 0:
                    return MeasureContainerFlow(block, blockIdx);
                default:
                    return block.ContentHeight;
            }
        }

        /// <summary>
        /// Whether <paramref name="block"/> fits on the current page when the flow has
        /// already consumed <paramref name="usedOnPage"/> and the preceding sibling is
        /// <paramref name="prevBlock"/> (for the margin collapse). Used by the
        /// AvoidBreakAfter look-ahead to decide whether the current block would be the
        /// last one on the page.
        /// </summary>
        /// <summary>
        /// Can <paramref name="block"/> START on a page that already has
        /// <paramref name="usedOnPage"/> px used (after the collapsed gap with
        /// <paramref name="prevBlock"/>)? A block starts when its first line (text),
        /// whole box (image/hr), or first child (container) fits the page remainder.
        /// Unlike a full-fit test this returns true when the block would break
        /// inside — exactly the case where a break-after:avoid must NOT trigger.
        /// </summary>
        private bool CanStartOnPage(LayoutBlock block, float usedOnPage, LayoutBlock? prevBlock)
        {
            var topMargin = LayoutBlock.EffectiveTopMargin(block);
            var isSpacer = LayoutBlock.IsZeroHeightSpacer(block);
            var ownTop = isSpacer && topMargin > 0f
                ? Math.Max(block.MarginTop, block.MarginBottom)
                : topMargin;
            var prevBottom = prevBlock is null ? 0f : LayoutBlock.EffectiveBottomMargin(prevBlock);
            var gap = Math.Max(prevBottom, ownTop);
            var start = usedOnPage + gap + block.PaddingTop;
            if (start > PageHeight) return false;

            switch (block.Kind)
            {
                case BlockKind.Container:
                    if (block.Children.Count == 0) return true;
                    // The first child's top margin collapses into the container's top
                    // margin (already in the container's effective top), so no extra gap.
                    return CanStartOnPage(block.Children[0], start, null);
                case BlockKind.Text:
                    if (block.LineCount == 0) return block.ContentHeight <= 0f;
                    var firstLine = block.LineHeights is { Length: > 0 } ? block.LineHeights[0] : block.LineHeightPx;
                    return start + firstLine <= PageHeight;
                default:
                    // Box (image, hr): atomic, must fit entirely to start.
                    return start + block.ContentHeight + block.PaddingBottom <= PageHeight;
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
                scratch.PlaceBlock(container.Children[ci], blockIdx, ci, ShouldTrimTopMargin(container, ci),
                    nextBlock: ci + 1 < container.Children.Count ? container.Children[ci + 1] : null,
                    ref childPrevBottom, ref childPrevZeroHeight);
            scratch.ConsumeContainerTailMargin(container);
            return scratch.Paged ? float.PositiveInfinity : scratch.UsedOnPage;
        }

        private void RecordSlice(int blockIdx, int childIdx, int lineStart, int lineCount, float y, float height)
        {
            if (DryRun) return;   // measurement only: the caller still updates UsedOnPage
            EnsurePage(CurrentPage);
            PageBlocks[CurrentPage].Add(new PageBlockSlice
            {
                BlockIndex = blockIdx,
                ChildIndex = childIdx,
                LineStart = lineStart,
                LineCount = lineCount,
                Y = y,
                Height = height
            });
        }

        private void EnsurePage(int idx)
        {
            if (DryRun) return;
            while (PageBlocks.Count <= idx)
            {
                PageBlocks.Add([]);
            }
        }

        /// <summary>
        /// Materializes the current page if it received content (e.g. a padding
        /// overflow) even though it received no slice. A page that is empty for
        /// real (UsedOnPage == 0) is intentionally not emitted — e.g. the page a
        /// trailing PageBreakAfter starts. (A zero-height block with margins that
        /// forced the page break materializes its page itself, via its slice in
        /// PlaceBox.)
        /// </summary>
        public void MaterializeCurrentPageIfUsed()
        {
            if (UsedOnPage > 0f)
                EnsurePage(CurrentPage);
        }

        private void NewPage()
        {
            if (DryRun) Paged = true;
            CurrentPage++;
            UsedOnPage = 0f;
        }

        private void PlaceText(LayoutBlock block, int blockIdx, int childIdx)
        {
            var lineHeight = block.LineHeightPx;
            if (lineHeight <= 0f)
            {
                // Degenerate zero-height lines: fall back to box placement.
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
                // Place whole lines while their BOTTOM edge fits the page remainder
                // (bottom-fits). The body sets orphans:1 / widows:1, so there is no
                // widow/orphan adjustment — a line breaks to the next page as soon as
                // it no longer fits, exactly as the browser does.
                var fit = 0;
                var sliceHeight = 0f;
                for (var li = placed; li < block.LineCount; li++)
                {
                    var lineH = havePerLine ? perLine![li] : lineHeight;
                    if (sliceHeight + lineH > space) break;
                    sliceHeight += lineH;
                    fit++;
                }

                if (fit <= 0)
                {
                    var nextLineHeight = havePerLine ? perLine![placed] : lineHeight;
                    if (nextLineHeight > PageHeight)
                    {
                        // A line taller than a whole page fits no page. Browser: at
                        // the top of a page it stays in place and the overflow is
                        // clipped; mid-page it moves to a fresh page, clipped there.
                        if (UsedOnPage > 0f) NewPage();
                        RecordSlice(blockIdx, childIdx, placed, 1, UsedOnPage, nextLineHeight);
                        UsedOnPage = PageHeight;
                        remaining--;
                        placed++;
                        // The clipped line filled the page: start the next one and
                        // skip the fit bookkeeping below (with remaining == 0 it
                        // would record a phantom zero-height slice).
                        if (UsedOnPage >= PageHeight) NewPage();
                        continue;
                    }
                    else
                    {
                        // No whole line fits here: the lines move whole to the next page.
                        NewPage();
                        continue;
                    }
                }

                if (fit >= remaining)
                {
                    // Uniform: the whole block height is remaining × lineHeight (original
                    // formula). Per-line: sliceHeight already accumulated the exact sum.
                    var fullHeight = havePerLine ? sliceHeight : remaining * lineHeight;
                    RecordSlice(blockIdx, childIdx, placed, remaining, UsedOnPage, fullHeight);
                    UsedOnPage += fullHeight;
                    remaining = 0;
                }
                else
                {
                    RecordSlice(blockIdx, childIdx, placed, fit, UsedOnPage, sliceHeight);
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
                // Zero-height block: it occupies space only through its margins/padding.
                // With none of them it occupies nothing — the browser creates no column
                // for it, so nothing is recorded and no page is materialized (old behavior).
                // With margins/padding it gets a zero-height slice (debug trace). Recording
                // the slice also materializes the page when the block FORCED the page break
                // (its margin did not fit below the previous sibling): the browser spills it
                // into a fresh column and counts that column. At the top of a page created by
                // the PREVIOUS block's overflow (the margin is trimmed at the boundary) the
                // browser does not create a column for a zero-height block, so the slice is
                // skipped unless the page is already materialized.
                var occupiesSpace = block.MarginTop > 0f || block.MarginBottom > 0f
                    || block.PaddingTop > 0f || block.PaddingBottom > 0f;
                if (occupiesSpace && (ForcedPageBreak || UsedOnPage > 0f || CurrentPage < PageBlocks.Count))
                    RecordSlice(blockIdx, childIdx, 0, 0, UsedOnPage, 0f);
                return;
            }

            if (UsedOnPage + h <= PageHeight)
            {
                RecordSlice(blockIdx, childIdx, 0, 0, UsedOnPage, h);
                UsedOnPage += h;
                if (UsedOnPage >= PageHeight) NewPage();
                return;
            }

            if (h <= PageHeight)
            {
                // Does not fit the page remainder but fits a fresh page: move whole.
                NewPage();
                RecordSlice(blockIdx, childIdx, 0, 0, 0f, h);
                UsedOnPage += h;
                if (UsedOnPage >= PageHeight) NewPage();
                return;
            }

            // Taller than a page: the browser keeps the (unbreakable) box in
            // place when it starts at the top of a page and clips the overflow;
            // mid-page it moves to a fresh page, clipped there.
            if (UsedOnPage > 0f) NewPage();
            RecordSlice(blockIdx, childIdx, 0, 0, UsedOnPage, h);
            UsedOnPage = PageHeight;
            if (UsedOnPage >= PageHeight) NewPage();
        }
    }
}
