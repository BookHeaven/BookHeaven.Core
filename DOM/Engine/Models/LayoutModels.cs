namespace BookHeaven.Core.DOM.Engine.Models;

/// <summary>
/// The category of content a block carries. Determines how the page splitter
/// fragments the block: <see cref="BlockKind.Text"/> blocks split at line
/// boundaries, everything else is moved whole to a fresh page or pixel-sliced.
/// </summary>
public enum BlockKind
{
    Text,
    Image,
    Container
}

[Flags]
public enum LayoutBlockFlags
{
    None = 0,
    PageBreakBefore = 1 << 0,
    PageBreakAfter = 1 << 1,
    AvoidBreakInside = 1 << 2,
    AvoidBreakAfter = 1 << 3
}

/// <summary>
/// A layout block: the analogue of a browser block box. Each top-level element
/// (header, paragraph, image, table, ...) produces exactly one block with its
/// margin and padding stored EXPLICITLY, instead of being an implicit gap between
/// render lines.
/// </summary>
/// <remarks>
/// Geometry (all values in px, flow-relative coordinates):
/// <code>
///             ┌─────────────────────────────────┐
///  MarginTop  │  ┌───────────────────────────┐  │   MarginTop/Bottom are the
///             │  │  PaddingTop              │  │   element's OWN margins (as
///             │  │  ┌─────────────────────┐ │  │   in CSS); the space actually
///             │  │  │ Content (height)    │ │  │   consumed in the flow is the
///             │  │  │   text: N lines     │ │  │   collapsed margin — see
///             │  │  │   image/container   │ │  │   EffectiveTopMargin /
///             │  │  └─────────────────────┘ │  │   EffectiveBottomMargin.
///             │  │  PaddingBottom           │  │
///             │  └───────────────────────────┘  │
///  MarginBottom└───────────────────────────────┘
/// </code>
/// Text blocks additionally carry <see cref="LineCount"/> / <see cref="LineHeightPx"/>
/// so the page splitter can fragment them at line boundaries. Container blocks keep
/// their children in <see cref="Children"/>; the page splitter places them
/// individually (browser behaviour) instead of treating the container as one box.
/// </remarks>
public class LayoutBlock
{
    public string TagName = "";
    public BlockKind Kind = BlockKind.Text;

    /// <summary>
    /// Intrinsic content height in px (fractional, exact): the height of the
    /// content box alone (text lines, an image, or an explicit spacer height).
    /// The block carries no flow coordinates — the page splitter places it.
    /// </summary>
    public float ContentHeight;

    /// <summary>
    /// Own top margin, in px, exactly as the style sheet computes it (like
    /// getComputedStyle). For flow placement use
    /// <see cref="EffectiveTopMargin"/>: a container without padding-top also
    /// folds in its first child's top margin (CSS parent-child collapse).
    /// </summary>
    public float MarginTop;

    /// <summary>
    /// Own bottom margin, in px, exactly as the style sheet computes it. For
    /// flow placement use <see cref="EffectiveBottomMargin"/>: a container
    /// without padding-bottom also folds in its last child's bottom margin
    /// (CSS parent-child collapse).
    /// </summary>
    public float MarginBottom;

    public float PaddingTop;
    public float PaddingBottom;

    /// <summary>
    /// True when the block is a CSS float (<c>float: left/right</c>). Floats are out
    /// of flow: their margins do NOT collapse with the parent's margins (so a
    /// padding-less container does not fold a float child's margin into its effective
    /// margin), and a float child never blocks the parent-child collapse run of the
    /// in-flow children that follow it.
    /// </summary>
    public bool Floats;

    /// <summary>Number of measured text lines (text blocks only; non-text blocks use 0).</summary>
    public int LineCount;

    /// <summary>Height of one text line in px (text blocks only). For mixed-size blocks this is the TALLEST line.</summary>
    public float LineHeightPx;

    /// <summary>
    /// Per-line box heights in px (text blocks only, browser model: a line is as tall as its
    /// tallest inline box). Null for uniform-height blocks — the splitter uses
    /// <see cref="LineHeightPx"/> for every line, which is both faster and bit-identical
    /// to the pre-run behavior.
    /// </summary>
    public float[]? LineHeights;

    /// <summary>
    /// Child blocks for container blocks (empty for leaves).
    /// </summary>
    public List<LayoutBlock> Children = [];

    public LayoutBlockFlags Flags;

    /// <summary>Footnote link references found inside the block (dead data path, kept for parity).</summary>
    public List<string> Links = [];

    /// <summary>
    /// Effective top margin for flow placement: the block's own top margin — except
    /// that, like a browser, a container WITHOUT padding-top lets its first child's
    /// top margin collapse through it, so the effective margin is the max of both
    /// (recursively for nested containers). That child's top margin is therefore NOT
    /// added again inside the container.
    /// </summary>
    public static float EffectiveTopMargin(LayoutBlock block)
    {
        if (block.Kind == BlockKind.Container && block.Children.Count > 0 && block.PaddingTop <= 0f)
            return Math.Max(block.MarginTop, CollapsedLeadingMargin(block.Children));
        return block.MarginTop;
    }

    /// <summary>
    /// Effective bottom margin for flow placement: the block's own bottom margin —
    /// except that, like a browser, a container WITHOUT padding-bottom lets its last
    /// child's bottom margin collapse through it, so the effective margin is the max
    /// of both (recursively). That child's bottom margin is therefore NOT counted
    /// inside the container's content box.
    /// </summary>
    public static float EffectiveBottomMargin(LayoutBlock block)
    {
        if (block.Kind == BlockKind.Container && block.Children.Count > 0 && block.PaddingBottom <= 0f)
            return Math.Max(block.MarginBottom, CollapsedTrailingMargin(block.Children));
        return block.MarginBottom;
    }

    /// <summary>
    /// True when the block is a zero-height spacer: no content, no padding, no border.
    /// Such a block collapses its top and bottom margins through itself, so the margins
    /// of the blocks on either side merge into a single gap (CSS margin collapsing).
    /// A float is never a spacer: it is out of flow and its margins do not collapse
    /// through it.
    /// </summary>
    public static bool IsZeroHeightSpacer(LayoutBlock block)
        => !block.Floats
           && block.Kind == BlockKind.Text
           && block.LineCount == 0
           && block.ContentHeight <= 0f
           && block.PaddingTop <= 0f
           && block.PaddingBottom <= 0f;

    /// <summary>
    /// The margin that collapses out of the TOP of a child sequence: the max of the top
    /// margins of the leading run of zero-height spacers, plus the top margin of the
    /// first content child that terminates the run. A zero-height spacer collapses its
    /// top and bottom margins through itself, so the collapse continues to the next
    /// sibling; the first non-zero-height child stops the run (its top margin is folded
    /// in, but the run does not continue past it).
    /// </summary>
    private static float CollapsedLeadingMargin(List<LayoutBlock> children)
    {
        var max = 0f;
        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            // A float is out of flow: its margin does not collapse with the parent's,
            // so it is skipped (it neither contributes nor blocks the collapse run).
            if (child.Floats) continue;
            max = Math.Max(max, EffectiveTopMargin(child));
            if (!IsZeroHeightSpacer(child)) break;
            max = Math.Max(max, EffectiveBottomMargin(child));
        }
        return max;
    }

    /// <summary>
    /// The margin that collapses out of the BOTTOM of a child sequence: the mirror of
    /// <see cref="CollapsedLeadingMargin"/> — the max of the bottom margins of the
    /// trailing run of zero-height spacers, plus the bottom margin of the last content
    /// child that terminates the run.
    /// </summary>
    private static float CollapsedTrailingMargin(List<LayoutBlock> children)
    {
        var max = 0f;
        for (var i = children.Count - 1; i >= 0; i--)
        {
            var child = children[i];
            // A float is out of flow: its margin does not collapse with the parent's,
            // so it is skipped (it neither contributes nor blocks the collapse run).
            if (child.Floats) continue;
            max = Math.Max(max, EffectiveBottomMargin(child));
            if (!IsZeroHeightSpacer(child)) break;
            max = Math.Max(max, EffectiveTopMargin(child));
        }
        return max;
    }
}

/// <summary>
/// A fragment of a block placed on a specific page. Text fragments carry the
/// line range; image/container fragments use <c>LineCount = 0</c> (whole move
/// or pixel slice). <see cref="Y"/> is relative to the page top.
/// </summary>
public class PageBlockSlice
{
    /// <summary>Index of the source block in the flat block list.</summary>
    public int BlockIndex;

    /// <summary>
    /// Index of the source block in its parent container's <see cref="LayoutBlock.Children"/>
    /// (slices produced from a container's children); -1 for top-level blocks.
    /// </summary>
    public int ChildIndex = -1;

    /// <summary>First line of the block contained in this slice (0-based).</summary>
    public int LineStart;

    /// <summary>Number of lines in this slice, or 0 for a pixel slice / whole block.</summary>
    public int LineCount;

    /// <summary>Y position of the fragment relative to the page top.</summary>
    public float Y;

    /// <summary>Height of the fragment in px.</summary>
    public float Height;
}

public class BlockPageInfo
{ 
    /// <summary>The block fragments placed on this page, in reading order.</summary>
    public List<PageBlockSlice> Blocks = [];
}

public record BlockPageMap(List<BlockPageInfo> Pages);
