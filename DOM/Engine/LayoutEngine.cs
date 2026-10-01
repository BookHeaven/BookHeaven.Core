using BookHeaven.Core.DOM.CSS;
using BookHeaven.Core.DOM.CSS.Models;
using BookHeaven.Core.DOM.Engine.Helpers;
using BookHeaven.Core.DOM.Engine.Models;
using BookHeaven.Core.DOM.HTML;
using BookHeaven.Core.DOM.HTML.Models;
using BookHeaven.Core.DOM.Services;
using BookHeaven.Core.DOM.Text.Abstractions;
using BookHeaven.Core.DOM.Text.Helpers;
using BookHeaven.Core.DOM.Text.Measurers;
using Microsoft.Extensions.Options;

namespace BookHeaven.Core.DOM.Engine;

/// <summary>
/// The block layout engine on top of the lightweight Mini pipeline
/// (<see cref="HtmlParser"/> + <see cref="CssParser"/> +
/// <see cref="StyleResolver"/> + <see cref="CssLengthParser"/>): the
/// browser-style block model (one <see cref="LayoutBlock"/> per top-level
/// element, explicit margins/paddings, children measured independently for
/// <see cref="PageSplitter"/>), but
/// without AngleSharp — the HTML is parsed in a single pass and the CSS cascade
/// (specificity, source order, !important, inheritance, var()) is resolved
/// in-house.
///
/// The CSS pipeline mirrors the AngleSharp one so results stay comparable: the
/// same global stylesheet is injected, logical margins are expanded to physical
/// longhands before parsing, and chapter stylesheets are appended after it.
///
/// Pure CSS value parsing lives in <see cref="CssValueParser"/>; image geometry
/// in <see cref="ImageHelpers"/>; inline text-run building in
/// <see cref="TextRunBuilder"/>. This class owns the block model and the
/// per-document memoization.
/// </summary>
public sealed class LayoutEngine : IDisposable
{
    private const string BlockSelector = "p,div,h1,h2,h3,h4,h5,h6,li,ul,ol,table,blockquote,pre,section,article,header,footer,main,aside,nav,dl,dt,dd,figure,img,svg,image,picture,object,canvas,hr";

    // Derived from BlockSelector so the list can never drift apart. Tag names are
    // stored lowercase; the comparer makes the lookup case-insensitive.
    private static readonly HashSet<string> BlockTagSet =
        BlockSelector.Split(',').Select(t => t.Trim().ToLowerInvariant()).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private readonly PageCalculatorOptions _options;
    private readonly CoreOptions _coreOptions;
    private readonly ITextMeasurer _measurer;
    private readonly bool _ownsMeasurer;
    private readonly RenderContext _context;
    private readonly HtmlParser _parser;
    private readonly ParseScope _scope;
    private readonly CssLengthParser _lengthParser = new();

    // Per-document state, cleared per call. The key includes the element's structural
    // context (preceding siblings + ancestor chain) so sibling/descendant combinators
    // can't poison the cache across structurally-different elements.
    private readonly Dictionary<(string Tag, string Attrs, string? Inline, CssStyle? Parent, long SiblingCtx, long AncestorCtx), StyleEntry> _styleMemo = [];
    private Dictionary<Element, float>? _contentWidthMemo;
    private List<CssRule> _rules = [];
    private int[] _specificities = [];

    // Per-document indexes of the rules that target each pseudo-element (their last
    // compound is that pseudo). Precomputed once per document so the per-element
    // pseudo resolution scans only the (few) relevant rules, not the whole sheet.
    private List<int> _beforeRules = [];
    private List<int> _afterRules = [];

    /// <summary>
    /// A memoized resolution: the resolved style plus the hidden verdict, both pure
    /// functions of the element's signature (tag, attributes, inline style) and its
    /// parent's resolved style. Every element sharing a signature reuses one entry,
    /// so a chapter resolves only its distinct signatures (a few dozen), not its
    /// hundreds of elements.
    /// </summary>
    private sealed class StyleEntry
    {
        public required CssStyle Style;
        public required bool Hidden;
        /// <summary>Resolved <c>::before</c> style, or null when no rule targets it.</summary>
        public CssStyle? Before;
        /// <summary>Resolved <c>::after</c> style, or null when no rule targets it.</summary>
        public CssStyle? After;
    }

    public LayoutEngine(
        PageCalculatorOptions options,
        IOptions<CoreOptions> coreOptions,
        ITextMeasurer? sharedMeasurer = null,
        CssParser? sharedCssParser = null,
        ParseScope? scope = null)
    {
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Normalize();
        ArgumentNullException.ThrowIfNull(coreOptions);
        _coreOptions = coreOptions.Value;

        // Positive dimensions and font size, otherwise em/rem/% resolution breaks.
        _context = new RenderContext(
            ClampPositive(_options.PageWidthPx, PageCalculatorOptions.DefaultPageWidthPx),
            ClampPositive(_options.PageHeightPx, PageCalculatorOptions.DefaultPageHeightPx),
            ClampPositive(_options.RootFontSizePx, PageCalculatorOptions.DefaultRootFontSizePx));

        // The parser owns the HTML construction (global + chapter stylesheets injected
        // around the fragment); the engine only passes the fragment and styles down.
        // Caches are scoped to the CalculatePagesAsync call that owns this engine.
        _scope = scope ?? new ParseScope();
        _parser = new HtmlParser(_options, sharedCssParser, _scope);

        // A shared measurer (one per book, used by several parallel engines) is not
        // disposed by the engines using it; only the caller that created it owns it.
        _ownsMeasurer = sharedMeasurer == null;
        _measurer = sharedMeasurer ?? TextMeasurerFactory.CreateMeasurer(_coreOptions.DefaultFontDirectory);
    }

    private static float ClampPositive(float? value, float fallback) => value is > 0 ? value.Value : fallback;

    /// <summary>
    /// Generates a flat list of layout blocks (in flow order) for the given HTML.
    /// </summary>
    public IReadOnlyList<LayoutBlock> GenerateLayoutBlocks(string html, IReadOnlyList<string>? css = null, CancellationToken cancellationToken = default)
    {
        _styleMemo.Clear();
        _contentWidthMemo = null;

        var (doc, rules) = _parser.Parse(html, css);
        _rules = rules;
        _specificities = PrecomputeSpecificities(rules);
        (_beforeRules, _afterRules) = PrecomputePseudoRules(rules);
        cancellationToken.ThrowIfCancellationRequested();

        var body = doc.Body;
        var blocks = new List<LayoutBlock>();
        if (body == null) return blocks;

        var all = QueryAll(body, BlockSelector);
        if (all.Count == 0)
        {
            var text = GetVisibleText(body);
            var images = CollectInlineImages(body);
            if (string.IsNullOrEmpty(text) && images.Count == 0) return blocks;

            var block = BuildLeafBlock(text, body, images);
            if (block != null) blocks.Add(block);
            return blocks;
        }

        var matchedSet = new HashSet<Element>(all);
        var topLevel = all.Where(e => !HasAncestorInSet(e, matchedSet)).ToList();

        // A top-level INLINE image (no block ancestor) is not a block box: the
        // browser wraps it in an anonymous block. Consecutive ones share one.
        var pendingImages = new List<Element>();
        foreach (var element in topLevel)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsInlineImage(element))
            {
                pendingImages.Add(element);
                continue;
            }
            if (pendingImages.Count > 0)
            {
                var anon = BuildLeafBlock(string.Empty, body, pendingImages);
                if (anon != null) blocks.Add(anon);
                pendingImages.Clear();
            }
            ProcessElement(element, blocks, cancellationToken);
        }
        if (pendingImages.Count > 0)
        {
            var anon = BuildLeafBlock(string.Empty, body, pendingImages);
            if (anon != null) blocks.Add(anon);
        }

        return blocks;
    }

    /// <summary>Collects every element in the subtree (pre-order) whose tag is in the comma-separated selector.</summary>
    private static List<Element> QueryAll(Element root, string selector)
    {
        var tags = selector == BlockSelector ? BlockTagSet : selector.Split(',').Select(t => t.Trim().ToLowerInvariant()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new List<Element>();
        Collect(root, result);
        return result;

        void Collect(Element el, List<Element> sink)
        {
            if (tags.Contains(el.TagName)) sink.Add(el);
            foreach (var child in el.Children) Collect(child, sink);
        }
    }

    /// <summary>Precomputes each rule's selector specificity once per document (the cascade used to recompute it per rule × element pair).</summary>
    private static int[] PrecomputeSpecificities(List<CssRule> rules)
    {
        var specs = new int[rules.Count];
        for (var i = 0; i < rules.Count; i++)
            specs[i] = StyleResolver.Specificity(rules[i].Selector);
        return specs;
    }

    /// <summary>
    /// Indexes of the rules whose last compound is a <c>::before</c> / <c>::after</c>
    /// pseudo-element, per document. The pseudo resolution scans only these (a few),
    /// not the whole sheet.
    /// </summary>
    private static (List<int> Before, List<int> After) PrecomputePseudoRules(List<CssRule> rules)
    {
        var before = new List<int>();
        var after = new List<int>();
        for (var i = 0; i < rules.Count; i++)
        {
            switch (rules[i].Selector.Pseudo)
            {
                case PseudoKind.Before: before.Add(i); break;
                case PseudoKind.After: after.Add(i); break;
            }
        }
        return (before, after);
    }

    /// <summary>
    /// Memoized style entry. The resolved style is a pure function of the element's own
    /// signature (tag, full attribute set, inline style), its parent's resolved style,
    /// and its structural context: the preceding element-siblings (sibling combinators
    /// <c>+</c>/<c>~</c>) and the ancestor chain (descendant/child combinators). Two
    /// elements sharing all of these reuse one resolution; the parent is resolved first
    /// (top-down). The context hashes are what keep structurally-different elements —
    /// e.g. a <c>p</c> after an <c>h1</c> vs. a <c>p</c> after a <c>p</c> — from
    /// colliding on the same key.
    /// </summary>
    private StyleEntry GetStyleEntry(Element el)
    {
        var parent = el.ParentElement is { } p ? GetStyleEntry(p).Style : null;
        var key = (el.TagName, el.AttributeSignature, el.Style, parent, SiblingContext(el), AncestorContext(el));
        if (_styleMemo.TryGetValue(key, out var entry))
            return entry;
        var style = StyleResolver.Resolve(el, _rules, _specificities, parent, _scope);
        entry = new StyleEntry
        {
            Style = style,
            Hidden = CssValueParser.IsHidden(el.HasAttribute("hidden"), el.Style, style.GetPropertyValue("display")),
            // The pseudo-elements inherit from the element's own resolved style and
            // match only the rules whose last compound is that pseudo.
            Before = ResolvePseudo(el, PseudoKind.Before, style),
            After = ResolvePseudo(el, PseudoKind.After, style)
        };
        _styleMemo[key] = entry;
        return entry;
    }

    /// <summary>
    /// Resolves a pseudo-element's style against the (few) rules that target it,
    /// inheriting from the element's resolved style. Returns null when no rule
    /// targets the pseudo (nothing to resolve).
    /// </summary>
    private CssStyle? ResolvePseudo(Element el, PseudoKind pseudo, CssStyle parent)
    {
        var indexes = pseudo == PseudoKind.Before ? _beforeRules : _afterRules;
        if (indexes.Count == 0)
            return null;
        var rules = new List<CssRule>(indexes.Count);
        var specs = new int[indexes.Count];
        for (var i = 0; i < indexes.Count; i++)
        {
            rules.Add(_rules[indexes[i]]);
            specs[i] = _specificities[indexes[i]];
        }
        return StyleResolver.ResolvePseudo(el, pseudo, rules, specs, parent, _scope);
    }

    /// <summary>
    /// Fingerprint of the element's preceding element-siblings. Sibling combinators
    /// (<c>+</c>, <c>~</c>) match against these, so elements with different left
    /// contexts must not share a memo entry even when their own signature and parent
    /// style are identical.
    /// </summary>
    private static long SiblingContext(Element el)
    {
        var parent = el.ParentElement;
        if (parent is null)
            return 0;
        var hash = 0L;
        foreach (var node in parent.ChildNodes)
        {
            if (node == el)
                break;
            if (node is Element sibling)
                hash = CombineHash(hash, sibling.TagName, sibling.AttributeSignature);
        }
        return hash;
    }

    /// <summary>
    /// Fingerprint of the element's ancestor chain. Descendant/child combinators match
    /// against ancestor tags and attributes, so the chain is part of the memo key.
    /// </summary>
    private static long AncestorContext(Element el)
    {
        var hash = 0L;
        for (var a = el.ParentElement; a is not null; a = a.ParentElement)
            hash = CombineHash(hash, a.TagName, a.AttributeSignature);
        return hash;
    }

    private static long CombineHash(long hash, string a, string b)
    {
        unchecked
        {
            hash = (hash * 1000003) ^ (uint)a.GetHashCode();
            hash = (hash * 1000003) ^ (uint)b.GetHashCode();
            return hash;
        }
    }

    /// <summary>Computed style, memoized per (signature, parent style) — see <see cref="GetStyleEntry"/>.</summary>
    private CssStyle GetComputedStyle(Element el) => GetStyleEntry(el).Style;

    private bool IsHidden(Element el) => GetStyleEntry(el).Hidden;

    private static bool IsBlockLevelTag(string tagName) => BlockTagSet.Contains(tagName);

    private static bool HasAncestorInSet(Element el, HashSet<Element> set)
    {
        var p = el.ParentElement;
        while (p != null)
        {
            if (set.Contains(p)) return true;
            p = p.ParentElement;
        }
        return false;
    }

    /// <summary>
    /// Resolved content width of an element, per document: it depends only on the
    /// element and its ancestor chain, so siblings (and repeated lookups of the same
    /// parent) reuse one computation.
    /// </summary>
    private float GetContainingBlockContentWidth(Element el)
    {
        if (el.ParentElement is not { } parent) return _options.PageWidthPx;
        if (_contentWidthMemo is not null && _contentWidthMemo.TryGetValue(parent, out var cached))
            return Math.Min(_options.PageWidthPx, cached > 0 ? cached : _options.PageWidthPx);
        var value = ResolveAncestorContentWidth(parent);
        (_contentWidthMemo ??= []).Add(parent, value);
        return Math.Min(_options.PageWidthPx, value > 0 ? value : _options.PageWidthPx);
    }

    private float ResolveAncestorContentWidth(Element parent)
    {
        var chain = new List<Element>();
        for (var p = parent; p != null; p = p.ParentElement) chain.Add(p);

        // The containing block of a static element is its parent's content box. Walk
        // the parent chain top-down so each ancestor's % width/margin/padding resolves
        // against ITS containing block (not the page): an ancestor with an explicit
        // width uses it, an auto-width ancestor is narrowed by its own horizontal
        // margins (CSS 2.1 shrink-to-fit) before its padding is removed.
        float cbWidth = _options.PageWidthPx;
        var result = -1f;
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            var el = chain[i];
            var pc = GetComputedStyle(el);
            // em units in an ancestor's box resolve against THAT ancestor's font-size
            // (rem stays root-relative). Falling back to the root context font-size
            // under-sizes em-based margins on elements that inherit a larger font.
            var emRef = ResolveFontSize(el);
            var pl = ParseLengthToPx(pc.GetPropertyValue("padding-left"), cbWidth, emRef) ?? 0f;
            var pr = ParseLengthToPx(pc.GetPropertyValue("padding-right"), cbWidth, emRef) ?? 0f;
            var ml = ParseLengthToPx(pc.GetPropertyValue("margin-left"), cbWidth, emRef) ?? 0f;
            var mr = ParseLengthToPx(pc.GetPropertyValue("margin-right"), cbWidth, emRef) ?? 0f;
            var pw = ParseLengthToPx(pc.GetPropertyValue("width"), cbWidth, emRef);
            var boxWidth = pw ?? (cbWidth - ml - mr);
            var contentW = boxWidth - pl - pr;

            cbWidth = contentW;
            if (contentW > 0) result = contentW;
        }
        return result;
    }

    private float? ParseLengthToPx(string? s, float reference, float? emReference = null)
        => _lengthParser.ParseLengthToPx(s, _context, reference, emReference);

    /// <summary>
    /// Recursively measures a block element, appending its block to <paramref name="sink"/>.
    /// The engine only measures INTRINSIC geometry (content height, lines, own
    /// margins/paddings, flags, children in document order); it does NOT place
    /// blocks or collapse margins — that is the page splitter's job.
    /// </summary>
    private void ProcessElement(Element el, List<LayoutBlock> sink, CancellationToken ct)
    {
        if (IsHidden(el)) return;

        // Image elements: a BLOCK-display image is a block box (replaced element);
        // an inline image (the UA default) is part of the parent's inline content
        // and is measured there (baseline model), so it emits nothing here.
        if (ImageHelpers.IsImageElement(el))
        {
            if (IsBlockDisplay(el))
            {
                var imgBlock = BuildLeafBlock(string.Empty, el);
                if (imgBlock != null) sink.Add(imgBlock);
            }
            return;
        }

        // Tables: a faithful Chromium computation (table-layout: auto, or the CSS
        // fixed algorithm when declared) predicts the table's column widths and
        // total height without a browser.
        if (el.TagName.Equals("table", StringComparison.OrdinalIgnoreCase))
        {
            var tableBlock = BuildTableBlock(el);
            if (tableBlock != null) sink.Add(tableBlock);
            return;
        }

        // Inline-display images are NOT block children: they belong to the
        // element's inline content (baseline model), not to its block flow.
        var blockChildren = el.Children
            .Where(c => IsBlockChild(c) && !IsHidden(c) && !IsInlineImage(c))
            .ToList();
        if (blockChildren.Count == 0)
        {
            // Leaf block: measure its full text content. An EMPTY leaf still
            // produces a block when it occupies space (see BuildLeafBlock).
            var text = GetVisibleText(el);
            var block = BuildLeafBlock(text, el);
            if (block != null) sink.Add(block);
            return;
        }

        // Container: the element's own vertical properties, as the style sheet
        // declares them. The splitter applies the CSS margin collapse.
        var layout = ComputeVerticalLayout(el);

        var childSink = new List<LayoutBlock>();

        // The ::before generated box is the element's first block child (document
        // order); it participates in the child flow like any other block child.
        var entry = GetStyleEntry(el);
        var beforeBox = BuildGeneratedBoxBlock(el, entry.Before, isBefore: true);
        if (beforeBox != null)
            childSink.Add(beforeBox);

        // Measure own inline text (text nodes not inside block children) and own
        // inline images. It is an anonymous box: no margins of its own.
        var ownText = TextRunBuilder.GetOwnInlineText(el, blockChildren, IsHidden);
        var ownImages = CollectInlineImages(el, blockChildren);
        if (!string.IsNullOrEmpty(ownText) || ownImages.Count > 0)
        {
            var ownBlock = BuildLeafBlock(ownText, el, ownImages, blockChildren);
            if (ownBlock != null)
            {
                // Anonymous box: no margins of its own. The element's margins belong
                // to the container, not to this box (resetting both avoids the
                // element's margin-bottom being counted twice).
                ownBlock.MarginTop = 0f;
                ownBlock.MarginBottom = 0f;
                childSink.Add(ownBlock);
            }
        }

        // Recurse into block children.
        for (var ci = 0; ci < blockChildren.Count; ci++)
        {
            ct.ThrowIfCancellationRequested();
            ProcessElement(blockChildren[ci], childSink, ct);
        }

        // The ::after generated box is the element's last block child (document
        // order); it participates in the child flow like any other block child.
        var afterBox = BuildGeneratedBoxBlock(el, entry.After, isBefore: false);
        if (afterBox != null)
            childSink.Add(afterBox);

        // A container's content height is the sum of its children's content boxes
        // (content + their vertical padding). An empty container occupies space
        // only through an explicit CSS height / <br> lines.
        float contentHeight = 0f;
        if (childSink.Count == 0)
        {
            contentHeight = ResolveSpacerGeometry(el).ContentHeight;
            if (contentHeight <= 0f && layout.MarginTop <= 0f && layout.MarginBottom <= 0f
                && layout.PaddingTop <= 0f && layout.PaddingBottom <= 0f)
                return;
        }
        else
        {
            // CSS margin collapse inside the content box: the first child's top
            // margin collapses through a padding-less container (it becomes part of
            // the container's effective top margin), sibling margins collapse to the
            // max, and with padding-bottom the last child's bottom margin stays
            // inside the box. The same rules the splitter applies when placing the
            // children, so ContentHeight equals the flow's height in the box.
            contentHeight = LayoutBlock.MeasureChildFlow(childSink, layout.PaddingTop, layout.PaddingBottom);
        }
        
        var computed = GetComputedStyle(el);
        var fontSize = ResolveFontSize(el);
        var heightPx = ParseLengthToPx(computed.GetPropertyValue("height"), _options.PageHeightPx, fontSize);
        var maxHeight = ParseLengthToPx(computed.GetPropertyValue("max-height"), _options.PageHeightPx, fontSize);

        // An explicit height, or a max-height that caps the children, FIXES the
        // content box: the children's overflow is clipped at the box (browser
        // behaviour), so the splitter must treat ContentHeight as authoritative.
        var fixedHeight = false;
        if (heightPx.HasValue)
        {
            contentHeight = heightPx.Value;
            fixedHeight = true;
        }
        if (contentHeight > maxHeight)
        {
            contentHeight = maxHeight.Value;
            fixedHeight = true;
        }

        var containerBlock = new LayoutBlock
        {
            TagName = el.TagName,
            Kind = BlockKind.Container,
            ContentHeight = contentHeight,
            MarginTop = layout.MarginTop,
            MarginBottom = layout.MarginBottom,
            PaddingTop = layout.PaddingTop,
            PaddingBottom = layout.PaddingBottom,
            LineCount = 0,
            LineHeightPx = 0f,
            Children = childSink,
            Flags = ToBreakFlags(computed) | (fixedHeight ? LayoutBlockFlags.FixedHeight : 0)
        };

        if (childSink.Count > 0)
        {
            if ((childSink[0].Flags & LayoutBlockFlags.PageBreakBefore) != 0) containerBlock.Flags |= LayoutBlockFlags.PageBreakBefore;
            if ((childSink[^1].Flags & LayoutBlockFlags.PageBreakAfter) != 0) containerBlock.Flags |= LayoutBlockFlags.PageBreakAfter;
        }

        sink.Add(containerBlock);
    }

    /// <summary>
    /// Computes a table's layout with a faithful Chromium algorithm —
    /// <c>table-layout: auto</c> by default, or the CSS <c>fixed</c> algorithm when
    /// declared — and emits it as a container whose children are the table's ROWS:
    /// one single-line text block per row (<c>LineCount = 1</c>,
    /// <c>LineHeightPx = row height</c>). The page splitter then places the rows
    /// individually — a page break lands on a row boundary, and a row that does not
    /// fit the page remainder moves whole to the next page (single-line text is
    /// monolithic), exactly as a browser breaks tall tables. The table's own
    /// padding/border bound the row flow as the container's PaddingTop/Bottom.
    /// </summary>
    private LayoutBlock? BuildTableBlock(Element el)
    {
        var computed = GetComputedStyle(el);
        var containerRefWidth = GetContainingBlockContentWidth(el);
        var fontSize = ResolveFontSize(el);

        var result = TableHelpers.Compute(
            el,
            containerRefWidth,
            GetComputedStyle,
            ResolveFontSize,
            IsHidden,
            _measurer,
            _options,
            _coreOptions,
            IsBlockDisplay,
            _context,
            _lengthParser);
        if (result is null) return null;

        var marginTop = TryGetComputedBoxPx(computed, "margin-top", containerRefWidth, fontSize) ?? 0f;
        var marginBottom = TryGetComputedBoxPx(computed, "margin-bottom", containerRefWidth, fontSize) ?? 0f;

        // One block per row: a single "line" as tall as the row. The splitter
        // treats single-line text as monolithic (moved whole, never split), so
        // page breaks inside the table land on row boundaries.
        var rows = new List<LayoutBlock>(result.RowHeights.Length);
        for (var r = 0; r < result.RowHeights.Length; r++)
        {
            var rowHeight = result.RowHeights[r];
            rows.Add(new LayoutBlock
            {
                TagName = "tr",
                Kind = BlockKind.Text,
                ContentHeight = rowHeight,
                LineCount = 1,
                LineHeightPx = rowHeight
            });
        }

        return new LayoutBlock
        {
            TagName = el.TagName,
            Kind = BlockKind.Container,
            ContentHeight = result.ContentHeight,
            MarginTop = marginTop,
            MarginBottom = marginBottom,
            PaddingTop = result.TopEdge,
            PaddingBottom = result.BottomEdge,
            LineCount = 0,
            LineHeightPx = 0f,
            Children = rows,
            Flags = ToBreakFlags(computed)
        };
    }

    /// <summary>
    /// Builds the block for a pseudo-element's generated box, or null when the box
    /// does not occupy space. A generated box exists only when <c>content</c> is set
    /// to a value other than <c>none</c>; it is a BLOCK child only when its
    /// <c>display</c> is explicitly block-like (the pseudo default is inline). The
    /// box's geometry (height, margins, paddings, float) comes from its resolved
    /// style. A float box is marked <see cref="LayoutBlock.Floats"/> so the splitter
    /// keeps its margins out of the parent's collapse.
    /// </summary>
    private LayoutBlock? BuildGeneratedBoxBlock(Element el, CssStyle? style, bool isBefore)
    {
        if (style is null)
            return null;

        // A generated box exists only when content is set to something other than none.
        var content = style.GetPropertyValue("content");
        if (string.IsNullOrWhiteSpace(content) || string.Equals(content, "none", StringComparison.OrdinalIgnoreCase))
            return null;

        // The pseudo default display is inline; only an EXPLICIT block-like display
        // makes the generated box a block child of the element.
        var display = style.GetPropertyValue("display");
        if (!style.IsExplicit("display") || !CssValueParser.IsBlockDisplay(display, el.TagName))
            return null;

        var fontSize = ResolveFontSize(el);
        var containerRefWidth = GetContainingBlockContentWidth(el);

        var marginTop = TryGetComputedBoxPx(style, "margin-top", containerRefWidth, fontSize) ?? 0f;
        var marginBottom = TryGetComputedBoxPx(style, "margin-bottom", containerRefWidth, fontSize) ?? 0f;
        var paddingTop = TryGetComputedBoxPx(style, "padding-top", containerRefWidth, fontSize) ?? 0f;
        var paddingBottom = TryGetComputedBoxPx(style, "padding-bottom", containerRefWidth, fontSize) ?? 0f;
        var heightPx = ParseLengthToPx(style.GetPropertyValue("height"), _options.PageHeightPx, fontSize) ?? 0f;

        var floatValue = style.GetPropertyValue("float");
        var floats = string.Equals(floatValue, "left", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(floatValue, "right", StringComparison.OrdinalIgnoreCase);

        return new LayoutBlock
        {
            TagName = el.TagName + (isBefore ? "::before" : "::after"),
            Kind = BlockKind.Text,
            ContentHeight = heightPx,
            MarginTop = marginTop,
            MarginBottom = marginBottom,
            PaddingTop = paddingTop,
            PaddingBottom = paddingBottom,
            LineCount = 0,
            LineHeightPx = 0f,
            Floats = floats
        };
    }

    /// <summary>
    /// The vertical space a LEAF element's generated boxes add to its block, folded
    /// in with CSS parent-child margin collapse. Returns the box height to add to the
    /// content, plus the margin to fold into the element's own margin:
    /// <list type="bullet">
    /// <item>With padding, the box's margin does NOT collapse with the element's — the
    /// full margin is added to the content (returned in <c>Height</c>), nothing folds.</item>
    /// <item>Without padding, the box's margin collapses with the element's own margin —
    /// the caller takes <c>max(ownMargin, MarginToFold)</c>, then adds <c>Height</c>.</item>
    /// <item>A float box's margin never collapses (out of flow) — it is added in full
    /// to <c>Height</c> and never folded.</item>
    /// </list>
    /// Returns (0, 0) when the element has no block-level generated box.
    /// </summary>
    private (float MarginToFold, float Height) GetGeneratedBoxContribution(Element el, bool hasPadding)
    {
        var entry = GetStyleEntry(el);
        var margin = 0f;
        var height = 0f;
        foreach (var (style, isBefore) in new[] { (entry.Before, true), (entry.After, false) })
        {
            var box = BuildGeneratedBoxBlock(el, style, isBefore);
            if (box is null) continue;
            var boxMargin = box.MarginTop + box.MarginBottom;
            height += box.ContentHeight + box.PaddingTop + box.PaddingBottom;
            if (box.Floats || hasPadding)
                height += boxMargin; // no collapse: the margin is inside the content box
            else
                margin = Math.Max(margin, boxMargin); // collapses with the element's margin
        }
        return (margin, height);
    }

    /// <summary>
    /// Measures a leaf element (text or image) and builds its block with its
    /// INTRINSIC geometry. Returns null when the element is hidden, or when it has
    /// no content AND occupies no space.
    /// </summary>
    private LayoutBlock? BuildLeafBlock(string text, Element el, IReadOnlyList<Element>? inlineImages = null, List<Element>? blockChildren = null)
    {
        var computed = GetComputedStyle(el);
        if (IsHidden(el)) return null;

        // Inline-display images in this element's content (collected by the caller,
        // or gathered here for the leaf path).
        var images = inlineImages ?? CollectInlineImages(el);

        var fontSize = ResolveFontSize(el);
        var bold = CssValueParser.IsBoldWeight(computed.GetPropertyValue("font-weight"));
        var italic = CssValueParser.IsItalicStyle(computed.GetPropertyValue("font-style"));
        var containerRefWidth = GetContainingBlockContentWidth(el);

        var flags = ToBreakFlags(computed);

        var marginTop = TryGetComputedBoxPx(computed, "margin-top", containerRefWidth, fontSize) ?? 0f;
        var marginBottom = TryGetComputedBoxPx(computed, "margin-bottom", containerRefWidth, fontSize) ?? 0f;
        var marginLeft = TryGetComputedBoxPx(computed, "margin-left", containerRefWidth, fontSize) ?? 0f;
        var marginRight = TryGetComputedBoxPx(computed, "margin-right", containerRefWidth, fontSize) ?? 0f;

        var paddingTop = TryGetComputedBoxPx(computed, "padding-top", containerRefWidth, fontSize) ?? 0f;
        var paddingBottom = TryGetComputedBoxPx(computed, "padding-bottom", containerRefWidth, fontSize) ?? 0f;
        var paddingLeft = TryGetComputedBoxPx(computed, "padding-left", containerRefWidth, fontSize) ?? 0f;
        var paddingRight = TryGetComputedBoxPx(computed, "padding-right", containerRefWidth, fontSize) ?? 0f;

        // The element's generated boxes (::before / ::after), folded into this block
        // with CSS parent-child margin collapse (see GetGeneratedBoxContribution).
        // Applied at every return point below.
        var (genMargin, genHeight) = GetGeneratedBoxContribution(el, hasPadding: paddingTop > 0f || paddingBottom > 0f);

        // Empty leaf (no text, no inline images, not an image element): it
        // occupies space only through an explicit CSS height, padding or margins.
        var spacerHeight = 0f;
        var spacerLines = 0;
        var spacerLineHeight = 0f;
        var isSpacerLeaf = string.IsNullOrEmpty(text) && !ImageHelpers.IsImageElement(el) && images.Count == 0;
        if (isSpacerLeaf)
        {
            // Empty elements occupy space through explicit height, padding, or margins.
            // Books use empty paragraphs as spacers; a browser renders their box.
            (spacerHeight, spacerLines, spacerLineHeight) = ResolveSpacerGeometry(el);
            if (spacerHeight <= 0f && paddingTop <= 0f && paddingBottom <= 0f
                && marginTop <= 0f && marginBottom <= 0f)
                return null;
        }

        // Compute available width for measuring.
        var availableWidth = Math.Min(_options.PageWidthPx, containerRefWidth);
        var wpx = ParseLengthToPx(computed.GetPropertyValue("width"), availableWidth, fontSize);
        var mwpx = ParseLengthToPx(computed.GetPropertyValue("max-width"), availableWidth, fontSize);
        if (wpx is > 0) availableWidth = Math.Min(_options.PageWidthPx, wpx.Value);
        else if (mwpx is > 0) availableWidth = Math.Min(_options.PageWidthPx, mwpx.Value);
        else
        {
            // Auto-width block: shrink-to-fit, the content box loses the margins.
            availableWidth -= (marginLeft + marginRight);
        }

        availableWidth -= (paddingLeft + paddingRight);
        if (availableWidth < 0) availableWidth = 0f;

        // Line-height: keep the LENGTH and UNITLESS forms separate (browser model).
        var (lineHeightLengthPx, lineHeightMultiplier) =
            CssValueParser.ResolveLineHeightForm(computed.GetPropertyValue("line-height"), fontSize, ParseLengthToPx, _lengthParser.ParseUnitless);
        var lineHeightPx = lineHeightLengthPx ?? lineHeightMultiplier * fontSize;
        if (lineHeightPx.HasValue) lineHeightPx = ITextMeasurer.QuantizeLineHeight(lineHeightPx.Value);

        // Letter-spacing and word-spacing.
        var letterSpacingPx = CssValueParser.TryParseSpacing(computed.GetPropertyValue("letter-spacing"), containerRefWidth, fontSize, ParseLengthToPx);
        var wordSpacingPx = CssValueParser.TryParseSpacing(computed.GetPropertyValue("word-spacing"), containerRefWidth, fontSize, ParseLengthToPx);

        // text-indent narrows the first line.
        float? firstLineWidthPx = null;
        var ti = computed.GetPropertyValue("text-indent");
        if (!string.IsNullOrWhiteSpace(ti))
        {
            var indentPx = ParseLengthToPx(ti, containerRefWidth, fontSize);
            if (indentPx is > 0)
            {
                firstLineWidthPx = Math.Max(0f, availableWidth - indentPx.Value);
            }
            else if (indentPx.HasValue)
            {
                // Negative indents extend the first line beyond the content edge.
                firstLineWidthPx = availableWidth - indentPx.Value;
            }
        }

        var contentHeight = 0f;

        // BLOCK-display image: a block-level replaced element, placed whole.
        if (ImageHelpers.IsImageElement(el))
        {
            // A wrapper (e.g. <figure>) sizes its box from the descendant image's
            // own CSS, so pass that image's computed style when el is not the
            // image itself.
            var srcEl = ImageHelpers.FindImageWithSrc(el);
            var srcComputed = srcEl != null ? GetComputedStyle(srcEl) : null;
            var (_, imgHeight) = ImageHelpers.MeasureImageSize(el, computed, ParseLengthToPx, _options, _coreOptions, availableWidth, containerRefWidth, fontSize, srcComputed);
            contentHeight = imgHeight + genHeight;
            if (genMargin > 0f) marginTop = Math.Max(marginTop, genMargin);

            return new LayoutBlock
            {
                TagName = el.TagName,
                Kind = BlockKind.Image,
                ContentHeight = contentHeight,
                MarginTop = marginTop,
                MarginBottom = marginBottom,
                PaddingTop = paddingTop,
                PaddingBottom = paddingBottom,
                LineCount = 0,
                LineHeightPx = lineHeightPx ?? 0f,
                Flags = flags
            };
        }

        // INLINE images: they sit in the element's inline content. A line box
        // containing an image of height H gets height max(lineHeight, the image's
        // vertical-align contribution) — see ImageHelpers.LineContribution for the
        // per-value model (baseline: H + strutDescent; sub/super/middle/length:
        // the shifted metrics unioned with the strut; top/bottom: H). The block is
        // a TEXT block (Kind.Text) so the splitter can break between its lines.
        if (images.Count > 0)
        {
            var resolvedLineHeight = ITextMeasurer.QuantizeLineHeight(lineHeightLengthPx
                ?? (lineHeightMultiplier ?? ITextMeasurer.NormalLineHeightMultiplier) * fontSize);
            var metrics = _measurer.GetFontMetrics(fontSize, TextRunBuilder.ToFontStyle(bold, italic));
            var strutDescent = ImageHelpers.ChromiumStrutDescent(resolvedLineHeight, metrics);

            // A LEADING inline image (before any text in document order) shares
            // the first line with the text: the text wraps around it and line 1
            // gets height max(textLine1, imageContribution), matching the browser.
            // Trailing images keep the documented approximation (their own line).
            var leadingImage = images.Count == 1 && HasLeadingImage(el, blockChildren);

            // The single leading image's geometry: its border-box width narrows
            // line 1, and its vertical-align contribution sets line 1's height.
            float leadingImgWidth = 0f, leadingImgContribution = 0f;
            if (leadingImage)
            {
                var imgStyle = GetComputedStyle(images[0]);
                var (w, h) = ImageHelpers.MeasureImageSize(images[0], imgStyle, ParseLengthToPx, _options, _coreOptions, availableWidth, containerRefWidth, fontSize);
                leadingImgWidth = w;
                leadingImgContribution = ImageHelpers.LineContribution(
                    imgStyle.GetPropertyValue("vertical-align"), h, resolvedLineHeight, strutDescent, fontSize, metrics.XHeight, ParseLengthToPx);
            }

            var imgLineHeights = new List<float>();
            if (!string.IsNullOrEmpty(text))
            {
                var textRuns = TextRunBuilder.Build(el, text, fontSize, bold, italic, GetComputedStyle, ParseLengthToPx, IsBlockChild, IsHidden);

                // A leading image narrows the first line by its border-box width
                // (box-sizing:border-box includes its padding, so the text starts
                // at the image's right edge — the browser gap is 0).
                var textFirstLine = leadingImage
                    ? Math.Max(0f, availableWidth - leadingImgWidth)
                    : firstLineWidthPx;

                var textMeas = textRuns.Count == 1
                    ? _measurer.Measure(textRuns[0].Text, textRuns[0].FontSizePx, availableWidth,
                        lineHeightLengthPx ?? (lineHeightMultiplier is null ? null : lineHeightMultiplier.Value * textRuns[0].FontSizePx),
                        letterSpacingPx, wordSpacingPx, textFirstLine, textRuns[0].Style, includeLineText: false)
                    : _measurer.MeasureRuns(textRuns, availableWidth, lineHeightLengthPx, lineHeightMultiplier, letterSpacingPx, wordSpacingPx, textFirstLine, includeLineText: false);

                for (var li = 0; li < textMeas.LineCount; li++)
                {
                    var h = textMeas.LineHeights.Length > 0 ? textMeas.LineHeights[li] : textMeas.LineHeightPx;
                    // Line 1: the leading image and the text share it; take the taller.
                    imgLineHeights.Add(leadingImage && li == 0 ? Math.Max(h, leadingImgContribution) : h);
                }
            }
            else if (leadingImage)
            {
                // Leading image with no text: the image alone on line 1.
                imgLineHeights.Add(Math.Max(resolvedLineHeight, leadingImgContribution));
            }

            if (!leadingImage)
            {
                // Trailing images (or multiple images): their own line(s).
                var imageBoxes = new List<(float Width, float Height, string? VerticalAlign)>(images.Count);
                foreach (var img in images)
                {
                    var imgStyle = GetComputedStyle(img);
                    var (imgWidth, imgHeight) = ImageHelpers.MeasureImageSize(img, imgStyle, ParseLengthToPx, _options, _coreOptions, availableWidth, containerRefWidth, fontSize);
                    imageBoxes.Add((imgWidth, imgHeight, imgStyle.GetPropertyValue("vertical-align")));
                }
                imgLineHeights.AddRange(ImageHelpers.BuildImageLines(imageBoxes, availableWidth, resolvedLineHeight, strutDescent, fontSize, metrics.XHeight, ParseLengthToPx));
            }

            contentHeight = (imgLineHeights.Count > 0 ? imgLineHeights.Sum() : 0f) + genHeight;
            if (genMargin > 0f) marginTop = Math.Max(marginTop, genMargin);
            return new LayoutBlock
            {
                TagName = el.TagName,
                Kind = BlockKind.Text,
                ContentHeight = contentHeight,
                MarginTop = marginTop,
                MarginBottom = marginBottom,
                PaddingTop = paddingTop,
                PaddingBottom = paddingBottom,
                LineCount = imgLineHeights.Count,
                LineHeightPx = imgLineHeights.Count > 0 ? imgLineHeights.Max() : 0,
                LineHeights = [.. imgLineHeights],
                Flags = flags
            };
        }

        if (string.IsNullOrEmpty(text))
        {
            // Spacer box: its content is the explicit CSS height. (<br> elements
            // flow through the text path as U+FFFC markers, so they never reach
            // here.)
            contentHeight = spacerHeight + genHeight;
            if (genMargin > 0f) marginTop = Math.Max(marginTop, genMargin);

            return new LayoutBlock
            {
                TagName = el.TagName,
                Kind = BlockKind.Text,
                ContentHeight = contentHeight,
                MarginTop = marginTop,
                MarginBottom = marginBottom,
                PaddingTop = paddingTop,
                PaddingBottom = paddingBottom,
                LineCount = spacerLines,
                LineHeightPx = spacerLineHeight,
                Flags = flags
            };
        }

        // Split the inline content into runs at their own font sizes (browser model).
        var runs = TextRunBuilder.Build(el, text, fontSize, bold, italic, GetComputedStyle, ParseLengthToPx, IsBlockChild, IsHidden);
        var meas = runs.Count == 1
            ? _measurer.Measure(runs[0].Text, runs[0].FontSizePx, availableWidth,
                lineHeightLengthPx ?? (lineHeightMultiplier is null ? null : lineHeightMultiplier.Value * runs[0].FontSizePx),
                letterSpacingPx, wordSpacingPx, firstLineWidthPx, runs[0].Style, includeLineText: false)
            : _measurer.MeasureRuns(runs, availableWidth, lineHeightLengthPx, lineHeightMultiplier, letterSpacingPx, wordSpacingPx, firstLineWidthPx, includeLineText: false);

        var lineHeights = meas.LineHeights.Length > 0 ? meas.LineHeights : null;
        for (var li = 0; li < meas.LineCount; li++)
        {
            contentHeight += lineHeights is not null ? lineHeights[li] : meas.LineHeightPx;
        }

        // Drop-cap (initial-letter): the first letter spans N lines, so the paragraph
        // is at least N natural lines tall.
        var initialLetter = CssValueParser.ParseInitialLetter(computed.GetPropertyValue("initial-letter"));
        if (initialLetter is > 0)
        {
            var metrics = _measurer.GetFontMetrics(fontSize, TextRunBuilder.ToFontStyle(bold, italic));
            var capheightRatio = metrics.CapHeight / fontSize;
            var naturalLineHeight = (int)_measurer.GetNaturalLineHeight(fontSize, TextRunBuilder.ToFontStyle(bold, italic));
            var letterCapHeight = (initialLetter.Value - 1) * lineHeightPx!.Value + metrics.CapHeight;
            var letterFontSize = letterCapHeight / capheightRatio;
            var dropCapHeight = lineHeightPx!.Value + naturalLineHeight - (initialLetter.Value - 1);
            if (dropCapHeight > contentHeight)
            {
                // The drop-cap box is taller than the text: the paragraph is N lines
                // tall (the drop-cap spans N natural lines). Emit N lines so the
                // splitter can break it across a page boundary, as the browser does.
                contentHeight = dropCapHeight;
                lineHeights = Enumerable.Repeat(dropCapHeight / initialLetter.Value, initialLetter.Value).ToArray();
            }
        }

        contentHeight += genHeight;
        if (genMargin > 0f) marginTop = Math.Max(marginTop, genMargin);

        return new LayoutBlock
        {
            TagName = el.TagName,
            Kind = BlockKind.Text,
            ContentHeight = contentHeight,
            MarginTop = marginTop,
            MarginBottom = marginBottom,
            PaddingTop = paddingTop,
            PaddingBottom = paddingBottom,
            LineCount = lineHeights?.Length ?? meas.LineCount,
            LineHeightPx = meas.LineHeightPx,
            LineHeights = lineHeights,
            Flags = flags
        };
    }

    /// <summary>
    /// The space a text-less element occupies as CONTENT: an explicit CSS
    /// <c>height</c> when declared; otherwise one line when the element contains
    /// non-collapsible content. (<c>&lt;br&gt;</c> children flow through the text
    /// path as U+FFFC markers, so they never reach here.)
    /// </summary>
    private (float ContentHeight, int LineCount, float LineHeightPx) ResolveSpacerGeometry(Element el)
    {
        var computed = GetComputedStyle(el);
        var fontSize = ResolveFontSize(el);

        var heightPx = ParseLengthToPx(computed.GetPropertyValue("height"), _options.PageHeightPx, fontSize);
        if (heightPx is > 0f) return (heightPx.Value, 0, 0f);

        // A whitespace-only element generates a line box only if it contains
        // non-collapsible content. The browser collapses the CSS-collapsible
        // whitespace characters to nothing, but a non-breaking space (&nbsp;,
        // U+00A0) is not collapsible and occupies a line. Any other
        // non-whitespace character (real text) also does.
        if (!HasNonCollapsibleContent(el, IsHidden))
            return (0f, 0, 0f);

        var (lhLengthPx, lhMultiplier) =
            CssValueParser.ResolveLineHeightForm(computed.GetPropertyValue("line-height"), fontSize, ParseLengthToPx, _lengthParser.ParseUnitless);
        var lineHeight = ITextMeasurer.QuantizeLineHeight(lhLengthPx ?? (lhMultiplier ?? ITextMeasurer.NormalLineHeightMultiplier) * fontSize);
        return (lineHeight, 1, lineHeight);
    }

    private static bool HasNonCollapsibleContent(Element el, Func<Element, bool> isHidden)
    {
        foreach (var node in el.ChildNodes)
        {
            if (node.NodeType == NodeType.Text)
            {
                if (node.TextContent.Any(c => !TextRunWrapper.IsCollapsibleWhitespace(c)))
                    return true;
            }
            else if (node is Element childEl)
            {
                // A display:none element (and its whole subtree) contributes no
                // content, so it cannot force a line box on an otherwise-empty box.
                if (!isHidden(childEl) && HasNonCollapsibleContent(childEl, isHidden))
                    return true;
            }
        }

        return false;
    }

    /// <summary>Maps the parsed CSS break properties to <see cref="LayoutBlockFlags"/>.</summary>
    private static LayoutBlockFlags ToBreakFlags(CssStyle style)
    {
        var (pageBreakBefore, pageBreakAfter, avoidBreakInside, avoidBreakAfter) = CssValueParser.ParseBreaks(style);
        LayoutBlockFlags flags = 0;
        if (pageBreakBefore) flags |= LayoutBlockFlags.PageBreakBefore;
        if (pageBreakAfter) flags |= LayoutBlockFlags.PageBreakAfter;
        if (avoidBreakInside) flags |= LayoutBlockFlags.AvoidBreakInside;
        if (avoidBreakAfter) flags |= LayoutBlockFlags.AvoidBreakAfter;
        return flags;
    }

    /// <summary>
    /// Computes the element's own vertical layout properties exactly as the style
    /// sheet declares them. The flow applies the CSS margin collapse on top.
    /// </summary>
    private (float MarginTop, float MarginBottom, float PaddingTop, float PaddingBottom) ComputeVerticalLayout(Element el)
    {
        var computed = GetComputedStyle(el);
        var fontSize = ResolveFontSize(el);
        var containerRefWidth = GetContainingBlockContentWidth(el);

        var marginTop = TryGetComputedBoxPx(computed, "margin-top", containerRefWidth, fontSize) ?? 0f;
        var marginBottom = TryGetComputedBoxPx(computed, "margin-bottom", containerRefWidth, fontSize) ?? 0f;

        var paddingTop = TryGetComputedBoxPx(computed, "padding-top", containerRefWidth, fontSize) ?? 0f;
        var paddingBottom = TryGetComputedBoxPx(computed, "padding-bottom", containerRefWidth, fontSize) ?? 0f;

        return (marginTop, marginBottom, paddingTop, paddingBottom);
    }

    private float ResolveFontSize(Element el)
    {
        var computed = GetComputedStyle(el);
        var fs = computed.GetPropertyValue("font-size");
        if (string.IsNullOrWhiteSpace(fs))
            return _options.RootFontSizePx;

        // em/% in font-size resolve against the parent's RESOLVED font-size.
        var parentFontSize = _options.RootFontSizePx;
        if (el.ParentElement is { } parent)
            parentFontSize = ResolveFontSize(parent);

        // An INHERITED font-size is already the parent's resolved size; re-parsing
        // its raw string (e.g. "max(2.42em, …)") would apply the em unit twice.
        if (!computed.IsExplicit("font-size"))
            return parentFontSize;

        // Keywords (small, larger, xx-large, …) are ratios of the parent's size.
        if (CssValueParser.TryParseFontSizeKeyword(fs, out var ratio))
            return parentFontSize * ratio;

        var parsed = ParseLengthToPx(fs, parentFontSize, parentFontSize);
        return parsed ?? parentFontSize;
    }

    // The resolver expands every logical property (margin-block, margin-block-start,
    // margin-inline, ...) into physical longhands at cascade time, so the engine reads
    // the physical longhand directly — no per-property fallback chain is needed.
    private float? TryGetComputedBoxPx(CssStyle computed, string name, float reference, float? emReference)
        => ParseLengthToPx(computed.GetPropertyValue(name), reference, emReference);

    /// <summary>
    /// True when the element's computed <c>display</c> is block-like. Images are
    /// inline by UA default; <c>figure</c> is block.
    /// </summary>
    private bool IsBlockDisplay(Element el)
        => CssValueParser.IsBlockDisplay(GetComputedStyle(el).GetPropertyValue("display"), el.TagName);

    /// <summary>
    /// True when the element is a block-level child of its parent. An explicit
    /// <c>display</c> wins; when none is set (the engine has no UA stylesheet) the
    /// tag-name heuristic approximates the UA default.
    /// </summary>
    private bool IsBlockChild(Element el)
    {
        var display = GetComputedStyle(el).GetPropertyValue("display");
        if (string.IsNullOrWhiteSpace(display))
            return IsBlockLevelTag(el.TagName);
        return CssValueParser.IsBlockDisplay(display, el.TagName);
    }

    /// <summary>
    /// The element's text content with surrounding CSS-collapsible whitespace trimmed,
    /// EXCLUDING display:none descendants (and their subtrees). Unlike
    /// <see cref="Element.TrimmedTextContent"/>, which is a cached, style-agnostic walk,
    /// this applies the cascade's hidden verdict so hidden inline content (e.g. a
    /// <c>display:none</c> link inside a list item) contributes no text.
    /// </summary>
    private string GetVisibleText(Element el) => Element.GetVisibleTextContent(el, IsHidden);

    /// <summary>An image element whose computed display is inline (the UA default).</summary>
    private bool IsInlineImage(Element el) => ImageHelpers.IsImageElement(el) && !IsBlockDisplay(el);

    /// <summary>
    /// Collects the inline-display image descendants of <paramref name="el"/>,
    /// skipping <paramref name="blockChildren"/> (their images belong to their
    /// own block flow, not to this element's inline content).
    /// </summary>
    private List<Element> CollectInlineImages(Element el, List<Element>? blockChildren = null)
    {
        List<Element> result = [];
        CollectInlineImagesRecursive(el, blockChildren, result);
        return result;
    }

    private void CollectInlineImagesRecursive(Element el, List<Element>? blockChildren, List<Element> result)
    {
        foreach (var child in el.Children)
        {
            if (IsHidden(child)) continue;
            if (blockChildren is { Count: > 0 } && blockChildren.Contains(child)) continue;
            if (ImageHelpers.IsImageElement(child) && !IsBlockDisplay(child))
            {
                result.Add(child);
                continue;
            }
            if (!IsBlockChild(child))
                CollectInlineImagesRecursive(child, null, result);
        }
    }

    /// <summary>
    /// True when the element's first inline content (document order, skipping
    /// hidden nodes and <paramref name="blockChildren"/>) is an image — a leading
    /// inline image that shares the first line with the text that follows it.
    /// </summary>
    private bool HasLeadingImage(Element el, List<Element>? blockChildren)
    {
        return Walk(el);

        bool Walk(Element e)
        {
            foreach (var child in e.ChildNodes)
            {
                if (child is Element childEl)
                {
                    if (IsHidden(childEl)) continue;
                    if (blockChildren is { Count: > 0 } && blockChildren.Contains(childEl)) continue;
                    if (ImageHelpers.IsImageElement(childEl) && !IsBlockDisplay(childEl))
                        return true;
                    if (!IsBlockChild(childEl))
                    {
                        if (Walk(childEl)) return true;
                        if (!string.IsNullOrWhiteSpace(GetVisibleText(childEl)))
                            return false;
                    }
                }
                else if (child.NodeType == NodeType.Text)
                {
                    if (!string.IsNullOrWhiteSpace(child.TextContent))
                        return false;
                }
            }
            return false;
        }
    }

    public void Dispose()
    {
        _contentWidthMemo = null;
        _styleMemo.Clear();
        _specificities = [];
        _beforeRules = [];
        _afterRules = [];

        // HarfBuzz/Skia measurers hold native resources; only the engine that created
        // the measurer releases them (a shared measurer outlives every engine that
        // uses it — see <see cref="PageCalculator"/>).
        if (_ownsMeasurer)
        {
            (_measurer as IDisposable)?.Dispose();
        }
        GC.SuppressFinalize(this);
    }
}
