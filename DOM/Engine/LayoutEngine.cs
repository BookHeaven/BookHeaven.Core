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
    public Task<IReadOnlyList<LayoutBlock>> GenerateLayoutBlocksAsync(string html, IReadOnlyList<string>? css = null, CancellationToken cancellationToken = default)
    {
        _styleMemo.Clear();
        _contentWidthMemo = null;

        var (doc, rules) = _parser.Parse(html, css);
        _rules = rules;
        _specificities = PrecomputeSpecificities(rules);
        cancellationToken.ThrowIfCancellationRequested();

        var body = doc.Body;
        var blocks = new List<LayoutBlock>();
        if (body == null) return Task.FromResult<IReadOnlyList<LayoutBlock>>(blocks);

        var all = QueryAll(body, BlockSelector);
        if (all.Count == 0)
        {
            var text = body.TrimmedTextContent;
            var images = CollectInlineImages(body);
            if (string.IsNullOrEmpty(text) && images.Count == 0) return Task.FromResult<IReadOnlyList<LayoutBlock>>(blocks);

            var block = BuildLeafBlock(text, body, images);
            if (block != null) blocks.Add(block);
            return Task.FromResult<IReadOnlyList<LayoutBlock>>(blocks);
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

        return Task.FromResult<IReadOnlyList<LayoutBlock>>(blocks);
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
            Hidden = CssValueParser.IsHidden(el.HasAttribute("hidden"), el.Style, style.GetPropertyValue("display"))
        };
        _styleMemo[key] = entry;
        return entry;
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

        // Tables: a faithful Chromium <c>table-layout: auto</c> computation predicts
        // the table's column widths and total height without a browser.
        if (el.TagName.Equals("table", StringComparison.OrdinalIgnoreCase))
        {
            var tableBlock = BuildTableBlock(el);
            if (tableBlock != null) sink.Add(tableBlock);
            return;
        }

        // Inline-display images are NOT block children: they belong to the
        // element's inline content (baseline model), not to its block flow.
        var blockChildren = el.Children
            .Where(c => IsBlockLevelTag(c.TagName) && !IsHidden(c) && !IsInlineImage(c))
            .ToList();
        if (blockChildren.Count == 0)
        {
            // Leaf block: measure its full text content. An EMPTY leaf still
            // produces a block when it occupies space (see BuildLeafBlock).
            var text = el.TrimmedTextContent;
            var block = BuildLeafBlock(text, el);
            if (block != null) sink.Add(block);
            return;
        }

        // Container: the element's own vertical properties, as the style sheet
        // declares them. The splitter applies the CSS margin collapse.
        var layout = ComputeVerticalLayout(el);

        var childSink = new List<LayoutBlock>();

        // Measure own inline text (text nodes not inside block children) and own
        // inline images. It is an anonymous box: no margins of its own.
        var ownText = TextRunBuilder.GetOwnInlineText(el, blockChildren);
        var ownImages = CollectInlineImages(el, blockChildren);
        if (!string.IsNullOrEmpty(ownText) || ownImages.Count > 0)
        {
            var ownBlock = BuildLeafBlock(ownText, el, ownImages);
            if (ownBlock != null)
            {
                ownBlock.MarginTop = 0f; // anonymous box: no top margin of its own
                childSink.Add(ownBlock);
            }
        }

        // Recurse into block children.
        for (var ci = 0; ci < blockChildren.Count; ci++)
        {
            ct.ThrowIfCancellationRequested();
            ProcessElement(blockChildren[ci], childSink, ct);
        }

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
            foreach (var child in childSink)
                contentHeight += child.ContentHeight + child.PaddingTop + child.PaddingBottom;
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
            Links = [],
            Children = childSink,
            Flags = ToBreakFlags(GetComputedStyle(el))
        };

        if (childSink.Count > 0)
        {
            if ((childSink[0].Flags & LayoutBlockFlags.PageBreakBefore) != 0) containerBlock.Flags |= LayoutBlockFlags.PageBreakBefore;
            if ((childSink[^1].Flags & LayoutBlockFlags.PageBreakAfter) != 0) containerBlock.Flags |= LayoutBlockFlags.PageBreakAfter;
        }

        sink.Add(containerBlock);
    }

    /// <summary>
    /// Computes a table's layout with a faithful Chromium <c>table-layout: auto</c>
    /// algorithm and emits it as a single block. The table's full border-box height
    /// is reported as <see cref="LayoutBlock.ContentHeight"/> so the page splitter
    /// can slice tall tables across pages; the internal column/row structure is
    /// consumed by the algorithm and not exposed as child blocks.
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
            _measurer,
            _options,
            _context,
            _lengthParser);
        if (result is null) return null;

        var marginTop = TryGetComputedBoxPx(computed, "margin-top", containerRefWidth, fontSize) ?? 0f;
        var marginBottom = TryGetComputedBoxPx(computed, "margin-bottom", containerRefWidth, fontSize) ?? 0f;

        return new LayoutBlock
        {
            TagName = el.TagName,
            Kind = BlockKind.Container,
            ContentHeight = result.BorderBoxHeight,
            MarginTop = marginTop,
            MarginBottom = marginBottom,
            PaddingTop = 0f,
            PaddingBottom = 0f,
            LineCount = 0,
            LineHeightPx = 0f,
            Links = [],
            Children = [],
            Flags = ToBreakFlags(computed)
        };
    }

    /// <summary>
    /// Measures a leaf element (text or image) and builds its block with its
    /// INTRINSIC geometry. Returns null when the element is hidden, or when it has
    /// no content AND occupies no space.
    /// </summary>
    private LayoutBlock? BuildLeafBlock(string text, Element el, IReadOnlyList<Element>? inlineImages = null)
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
            var (_, imgHeight) = ImageHelpers.MeasureImageSize(el, computed, ParseLengthToPx, _options, _coreOptions, availableWidth, containerRefWidth, fontSize);
            contentHeight = imgHeight;

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
                Flags = flags,
                Links = []
            };
        }

        // INLINE images: they sit in the element's inline content. A line box
        // containing an image of height H gets height max(lineHeight, H +
        // strutDescent) for a baseline-aligned image, where strutDescent = D +
        // (L - (A + D)) / 2 (the browser baseline model: the image's baseline is
        // the font baseline, so its bottom extends strutDescent below the line's
        // baseline); a bottom/top/middle-aligned image is contained by the line,
        // so the line gets max(lineHeight, H). The block is a TEXT block
        // (Kind.Text) so the splitter can break between its lines.
        if (images.Count > 0)
        {
            var resolvedLineHeight = ITextMeasurer.QuantizeLineHeight(lineHeightLengthPx
                ?? (lineHeightMultiplier ?? ITextMeasurer.NormalLineHeightMultiplier) * fontSize);
            var metrics = _measurer.GetFontMetrics(fontSize, TextRunBuilder.ToFontStyle(bold, italic));
            var strutDescent = ImageHelpers.ChromiumStrutDescent(resolvedLineHeight, metrics);

            var imgLineHeights = new List<float>();
            if (!string.IsNullOrEmpty(text))
            {
                // Mixed text + images: measure the text, then the images follow
                // on their own lines (documented approximation: the surveyed
                // epubs have no paragraph mixing text and images).
                var textRuns = TextRunBuilder.Build(el, text, fontSize, bold, italic, GetComputedStyle, ParseLengthToPx, IsBlockLevelTag);
                var textMeas = textRuns.Count == 1
                    ? _measurer.Measure(textRuns[0].Text, textRuns[0].FontSizePx, availableWidth,
                        lineHeightLengthPx ?? (lineHeightMultiplier is null ? null : lineHeightMultiplier.Value * textRuns[0].FontSizePx),
                        letterSpacingPx, wordSpacingPx, firstLineWidthPx, textRuns[0].Style, includeLineText: false)
                    : _measurer.MeasureRuns(textRuns, availableWidth, lineHeightLengthPx, lineHeightMultiplier, letterSpacingPx, wordSpacingPx, firstLineWidthPx, includeLineText: false);
                for (var li = 0; li < textMeas.LineCount; li++)
                    imgLineHeights.Add(textMeas.LineHeights.Length > 0 ? textMeas.LineHeights[li] : textMeas.LineHeightPx);
            }

            var imageBoxes = new List<(float Width, float Height, string? VerticalAlign)>(images.Count);
            foreach (var img in images)
            {
                var imgStyle = GetComputedStyle(img);
                var (imgWidth, imgHeight) = ImageHelpers.MeasureImageSize(img, imgStyle, ParseLengthToPx, _options, _coreOptions, availableWidth, containerRefWidth, fontSize);
                imageBoxes.Add((imgWidth, imgHeight, imgStyle.GetPropertyValue("vertical-align")));
            }

            imgLineHeights.AddRange(ImageHelpers.BuildImageLines(imageBoxes, availableWidth, resolvedLineHeight, strutDescent, fontSize,
                startOnNewLine: !string.IsNullOrEmpty(text)));

            contentHeight = imgLineHeights.Sum();
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
                LineHeightPx = imgLineHeights.Max(),
                LineHeights = [.. imgLineHeights],
                Flags = flags,
                Links = []
            };
        }

        if (string.IsNullOrEmpty(text))
        {
            // Spacer box: its content is the explicit CSS height. (<br> elements
            // flow through the text path as U+FFFC markers, so they never reach
            // here.)
            contentHeight = spacerHeight;

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
                Flags = flags,
                Links = []
            };
        }

        // Split the inline content into runs at their own font sizes (browser model).
        var runs = TextRunBuilder.Build(el, text, fontSize, bold, italic, GetComputedStyle, ParseLengthToPx, IsBlockLevelTag);
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
            Flags = flags,
            Links = []
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
        if (!HasNonCollapsibleContent(el))
            return (0f, 0, 0f);

        var (lhLengthPx, lhMultiplier) =
            CssValueParser.ResolveLineHeightForm(computed.GetPropertyValue("line-height"), fontSize, ParseLengthToPx, _lengthParser.ParseUnitless);
        var lineHeight = ITextMeasurer.QuantizeLineHeight(lhLengthPx ?? (lhMultiplier ?? ITextMeasurer.NormalLineHeightMultiplier) * fontSize);
        return (lineHeight, 1, lineHeight);
    }

    private static bool HasNonCollapsibleContent(Element el)
    {
        foreach (var node in el.ChildNodes)
        {
            if (node.NodeType == NodeType.Text)
            {
                if (node.TextContent.Any(c => !TextRunWrapper.IsCollapsibleWhitespace(c)))
                    return true;
            }
            else if (node is Element childEl && HasNonCollapsibleContent(childEl))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Maps the parsed CSS break properties to <see cref="LayoutBlockFlags"/>.</summary>
    private static LayoutBlockFlags ToBreakFlags(CssStyle style)
    {
        var (pageBreakBefore, pageBreakAfter, avoidBreakInside) = CssValueParser.ParseBreaks(style);
        LayoutBlockFlags flags = 0;
        if (pageBreakBefore) flags |= LayoutBlockFlags.PageBreakBefore;
        if (pageBreakAfter) flags |= LayoutBlockFlags.PageBreakAfter;
        if (avoidBreakInside) flags |= LayoutBlockFlags.AvoidBreakInside;
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
            if (!IsBlockLevelTag(child.TagName))
                CollectInlineImagesRecursive(child, null, result);
        }
    }

    public void Dispose()
    {
        _contentWidthMemo = null;
        _styleMemo.Clear();
        _specificities = [];

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
