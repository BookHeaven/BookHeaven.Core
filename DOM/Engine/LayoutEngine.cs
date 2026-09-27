using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using BookHeaven.Core.DOM.CSS;
using BookHeaven.Core.DOM.CSS.Models;
using BookHeaven.Core.DOM.Engine.Models;
using BookHeaven.Core.DOM.HTML;
using BookHeaven.Core.DOM.HTML.Models;
using BookHeaven.Core.DOM.Services;
using BookHeaven.Core.DOM.Text.Abstractions;
using BookHeaven.Core.DOM.Text.Measurers;
using Microsoft.Extensions.Options;
using SkiaSharp;
using FontStyle = BookHeaven.Core.DOM.Text.Abstractions.FontStyle;

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
/// </summary>
public sealed partial class LayoutEngine : IDisposable
{
    private const string BlockSelector = "p,div,h1,h2,h3,h4,h5,h6,li,ul,ol,table,blockquote,pre,section,article,header,footer,main,aside,nav,dl,dt,dd,figure,img,svg,image,picture,object,canvas,hr";

    // Derived from BlockSelector so the two lists can never drift apart.
    private static readonly HashSet<string> BlockTags =
        BlockSelector.Split(',').Select(t => t.Trim().ToUpperInvariant()).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private readonly PageCalculatorOptions _options;
    private readonly CoreOptions _coreOptions;
    private readonly ITextMeasurer _measurer;
    private readonly bool _ownsMeasurer;
    private readonly MiniRenderDevice _device;
    private readonly HtmlParser _parser;

    // Per-document state, cleared per call.
    private readonly Dictionary<(string Tag, string Attrs, string? Inline, MiniStyle? Parent), StyleEntry> _styleMemo = [];
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
        public required MiniStyle Style;
        public required bool Hidden;
    }

    public LayoutEngine(
        PageCalculatorOptions options,
        IOptions<CoreOptions> coreOptions,
        ITextMeasurer? sharedMeasurer = null)
    {
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Normalize();
        ArgumentNullException.ThrowIfNull(coreOptions);
        _coreOptions = coreOptions.Value;

        // Positive dimensions and font size, otherwise em/rem/% resolution breaks.
        _device = new MiniRenderDevice(
            ClampPositive(_options.PageWidthPx, PageCalculatorOptions.DefaultPageWidthPx),
            ClampPositive(_options.PageHeightPx, PageCalculatorOptions.DefaultPageHeightPx),
            ClampPositive(_options.RootFontSizePx, PageCalculatorOptions.DefaultRootFontSizePx));

        // The parser owns the HTML construction (global + chapter stylesheets injected
        // around the fragment); the engine only passes the fragment and styles down.
        _parser = new HtmlParser(_options);

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

    // Precomputed from BlockSelector so QueryAll doesn't split/allocate per call.
    private static readonly HashSet<string> BlockTagSet =
        BlockSelector.Split(',').Select(t => t.Trim().ToLowerInvariant()).ToHashSet(StringComparer.OrdinalIgnoreCase);

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
    /// Memoized style entry: the resolved style is a pure function of the element's
    /// signature (tag, full attribute set, inline style) and its parent's resolved
    /// style, so every element sharing a signature reuses one resolution. The parent
    /// is resolved first (top-down), and its memoized style is part of the key.
    /// </summary>
    private StyleEntry GetStyleEntry(Element el)
    {
        var parent = el.ParentElement is { } p ? GetStyleEntry(p).Style : null;
        var key = (el.TagName, el.AttributeSignature, el.Style, parent);
        if (_styleMemo.TryGetValue(key, out var entry))
            return entry;
        var style = StyleResolver.Resolve(el, _rules, _specificities, parent);
        entry = new StyleEntry { Style = style, Hidden = ComputeHidden(el, style) };
        _styleMemo[key] = entry;
        return entry;
    }

    /// <summary>Computed style, memoized per (signature, parent style) — see <see cref="GetStyleEntry"/>.</summary>
    private MiniStyle GetComputedStyle(Element el) => GetStyleEntry(el).Style;

    private bool IsHidden(Element el) => GetStyleEntry(el).Hidden;

    private static bool ComputeHidden(Element el, MiniStyle style)
    {
        if (el.HasAttribute("hidden")) return true;
        var inline = el.Style;
        if (!string.IsNullOrWhiteSpace(inline) && DisplayNoneRegex().IsMatch(inline))
            return true;
        var display = style.GetPropertyValue("display");
        return !string.IsNullOrWhiteSpace(display) && display.Trim().Equals("none", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBlockLevelTag(string tagName) => BlockTags.Contains(tagName);

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
            var pc = GetComputedStyle(chain[i]);
            var pl = ParseLengthToPx(pc.GetPropertyValue("padding-left"), cbWidth) ?? 0f;
            var pr = ParseLengthToPx(pc.GetPropertyValue("padding-right"), cbWidth) ?? 0f;
            var ml = ParseLengthToPx(pc.GetPropertyValue("margin-left"), cbWidth) ?? 0f;
            var mr = ParseLengthToPx(pc.GetPropertyValue("margin-right"), cbWidth) ?? 0f;
            var pw = ParseLengthToPx(pc.GetPropertyValue("width"), cbWidth);
            var boxWidth = pw ?? (cbWidth - ml - mr);
            var contentW = boxWidth - pl - pr;

            cbWidth = contentW;
            if (contentW > 0) result = contentW;
        }
        return result;
    }

    private float? ParseLengthToPx(string? s, float reference, float? emReference = null)
        => CssLengthParser.ParseLengthToPx(s, _device, reference, emReference);

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
        if (IsImageElement(el))
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
        var ownText = GetOwnInlineText(el, blockChildren);
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
            Flags = ParseBreakFlags(GetComputedStyle(el))
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

        var result = TableLayout.Compute(
            el,
            containerRefWidth,
            GetComputedStyle,
            ResolveFontSize,
            _measurer,
            _options,
            _device);
        if (result is null) return null;

        var marginTop = TryGetComputedMarginPx(computed, "margin-top", containerRefWidth, fontSize) ?? 0f;
        var marginBottom = TryGetComputedMarginPx(computed, "margin-bottom", containerRefWidth, fontSize) ?? 0f;

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
            Flags = ParseBreakFlags(computed)
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
        var bold = IsBoldWeight(computed.GetPropertyValue("font-weight"));
        var italic = IsItalicStyle(computed.GetPropertyValue("font-style"));
        var containerRefWidth = GetContainingBlockContentWidth(el);

        var flags = ParseBreakFlags(computed);

        var marginTop = TryGetComputedMarginPx(computed, "margin-top", containerRefWidth, fontSize) ?? 0f;
        var marginBottom = TryGetComputedMarginPx(computed, "margin-bottom", containerRefWidth, fontSize) ?? 0f;
        var marginLeft = TryGetComputedMarginPx(computed, "margin-left", containerRefWidth, fontSize) ?? 0f;
        var marginRight = TryGetComputedMarginPx(computed, "margin-right", containerRefWidth, fontSize) ?? 0f;

        var paddingTop = TryGetComputedPaddingPx(computed, "padding-top", containerRefWidth, fontSize) ?? 0f;
        var paddingBottom = TryGetComputedPaddingPx(computed, "padding-bottom", containerRefWidth, fontSize) ?? 0f;
        var paddingLeft = TryGetComputedPaddingPx(computed, "padding-left", containerRefWidth, fontSize) ?? 0f;
        var paddingRight = TryGetComputedPaddingPx(computed, "padding-right", containerRefWidth, fontSize) ?? 0f;

        // Empty leaf (no text, no inline images, not an image element): it
        // occupies space only through an explicit CSS height, padding or margins.
        var spacerHeight = 0f;
        var spacerLines = 0;
        var spacerLineHeight = 0f;
        var isSpacerLeaf = string.IsNullOrEmpty(text) && !IsImageElement(el) && images.Count == 0;
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
        var (lineHeightLengthPx, lineHeightMultiplier) = ResolveLineHeightForm(computed, fontSize);
        var lineHeightPx = lineHeightLengthPx ?? lineHeightMultiplier * fontSize;

        // Letter-spacing and word-spacing.
        float? letterSpacingPx = null;
        var ls = computed.GetPropertyValue("letter-spacing");
        if (!string.IsNullOrWhiteSpace(ls))
        {
            var t = ls.Trim();
            if (t.Equals("normal", StringComparison.OrdinalIgnoreCase)) letterSpacingPx = 0f;
            else
            {
                var parsed = ParseLengthToPx(ls, containerRefWidth, fontSize);
                if (parsed.HasValue) letterSpacingPx = parsed.Value;
            }
        }

        float? wordSpacingPx = null;
        var ws = computed.GetPropertyValue("word-spacing");
        if (!string.IsNullOrWhiteSpace(ws))
        {
            var t = ws.Trim();
            if (t.Equals("normal", StringComparison.OrdinalIgnoreCase)) wordSpacingPx = 0f;
            else
            {
                var parsed = ParseLengthToPx(ws, containerRefWidth, fontSize);
                if (parsed.HasValue) wordSpacingPx = parsed.Value;
            }
        }

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
        if (IsImageElement(el))
        {
            var (_, imgHeight) = MeasureImageSize(el, availableWidth, containerRefWidth, fontSize);
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
            var resolvedLineHeight = lineHeightLengthPx
                ?? (lineHeightMultiplier ?? ITextMeasurer.NormalLineHeightMultiplier) * fontSize;
            var metrics = _measurer.GetFontMetrics(fontSize, ToFontStyle(bold, italic));
            var strutDescent = ChromiumStrutDescent(resolvedLineHeight, metrics);

            var imgLineHeights = new List<float>();
            if (!string.IsNullOrEmpty(text))
            {
                // Mixed text + images: measure the text, then the images follow
                // on their own lines (documented approximation: the surveyed
                // epubs have no paragraph mixing text and images).
                var textRuns = BuildTextRuns(el, fontSize, bold, italic);
                var textMeas = textRuns.Count == 1
                    ? _measurer.Measure(textRuns[0].Text, textRuns[0].FontSizePx, availableWidth,
                        lineHeightLengthPx ?? (lineHeightMultiplier is null ? null : lineHeightMultiplier.Value * textRuns[0].FontSizePx),
                        letterSpacingPx, wordSpacingPx, firstLineWidthPx, textRuns[0].Style, includeLineText: false)
                    : _measurer.MeasureRuns(textRuns, availableWidth, lineHeightLengthPx, lineHeightMultiplier, letterSpacingPx, wordSpacingPx, firstLineWidthPx, includeLineText: false);
                for (var li = 0; li < textMeas.LineCount; li++)
                    imgLineHeights.Add(textMeas.LineHeights.Length > 0 ? textMeas.LineHeights[li] : textMeas.LineHeightPx);
            }

            imgLineHeights.AddRange(BuildImageLines(images, availableWidth, containerRefWidth, fontSize,
                resolvedLineHeight, strutDescent, startOnNewLine: !string.IsNullOrEmpty(text)));

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
        var runs = BuildTextRuns(el, fontSize, bold, italic);
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
        var initialLetter = ParseInitialLetter(computed.GetPropertyValue("initial-letter"));
        if (initialLetter is > 0)
        {
            var metrics = _measurer.GetFontMetrics(fontSize, ToFontStyle(bold, italic));
            var capheightRatio = metrics.CapHeight / fontSize;
            var naturalLineHeight = (int)_measurer.GetNaturalLineHeight(fontSize, ToFontStyle(bold, italic));
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

    /// <summary>Parses an <c>initial-letter</c> value into a line count, or null.</summary>
    private static int? ParseInitialLetter(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (float.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0f)
            return (int)Math.Ceiling(v);
        return null;
    }

    /// <summary>
    /// Computes the strut descent the way Chromium does for a line of height
    /// <paramref name="lineHeightPx"/>. Chromium rounds the font's natural
    /// metrics to integers (A = round(ascent), D = round(descent)), then
    /// distributes the line height as strutAscent = floor(L/2 + (A-D)/2) with
    /// the descent taking the remainder. A baseline-aligned image of height H
    /// then grows the line to max(strutAscent, H) + strutDescent.
    ///
    /// The symmetric "split the extra lead evenly" model the engine used before
    /// undershoots Chromium by up to ~1px on tall images, which is enough to
    /// shift a whole page of text. The rule here was derived empirically from
    /// headless-Chromium measurements:
    /// 25+ line-height/font-size data points all match it exactly.
    /// </summary>
    private static float ChromiumStrutDescent(float lineHeightPx, SKFontMetrics metrics)
    {
        var a = MathF.Round(-metrics.Ascent);
        var d = MathF.Round(metrics.Descent);
        var strutAscent = MathF.Floor(lineHeightPx / 2f + (a - d) / 2f);
        return lineHeightPx - strutAscent;
    }

    /// <summary>
    /// Greedily wraps inline image boxes into lines. Each line gets height
    /// max(lineHeight, tallest image contribution on the line): baseline-aligned
    /// images contribute height + strutDescent, bottom/top/middle-aligned images
    /// contribute their height alone. Adjacent images are separated by an
    /// approximate gap (0.25 × font size); the surveyed epubs have at most one
    /// image per paragraph, so the gap only matters in theory.
    /// </summary>
    private List<float> BuildImageLines(
        IReadOnlyList<Element> images, float availableWidth, float containerRefWidth, float fontSize,
        float lineHeightPx, float strutDescent, bool startOnNewLine)
    {
        var lineHeights = new List<float>(images.Count);
        var currentLine = 0f;
        // After text, the first image starts on a fresh line (documented
        // approximation: an image following text wraps to the next line).
        var currentLineWidth = startOnNewLine ? availableWidth : 0f;
        var imageGap = 0.25f * fontSize;

        foreach (var img in images)
        {
            var (iw, ih) = MeasureImageSize(img, availableWidth, containerRefWidth, fontSize);
            if (currentLineWidth > 0 && currentLineWidth + imageGap + iw > availableWidth)
            {
                lineHeights.Add(Math.Max(lineHeightPx, currentLine));
                currentLine = 0f;
                currentLineWidth = 0f;
            }
            currentLineWidth += (currentLineWidth > 0 ? imageGap : 0f) + iw;
            currentLine = Math.Max(currentLine, ImageLineContribution(img, ih, strutDescent));
        }

        lineHeights.Add(Math.Max(lineHeightPx, currentLine));
        return lineHeights;
    }

    /// <summary>
    /// How much an image of height <paramref name="ih"/> grows the line box that
    /// contains it, per its <c>vertical-align</c>: baseline (the default) adds
    /// the font's strut descent, bottom/top/middle do not.
    /// </summary>
    private float ImageLineContribution(Element img, float ih, float strutDescent)
    {
        var va = GetComputedStyle(img).GetPropertyValue("vertical-align");
        var baseline = string.IsNullOrWhiteSpace(va)
            || va.Trim().Equals("baseline", StringComparison.OrdinalIgnoreCase);
        return baseline ? ih + strutDescent : ih;
    }

    /// <summary>
    /// Computes the box size of an image element (CSS replaced-element model):
    /// computed width/height, then attributes, then the intrinsic size from the
    /// cached file, deriving the missing dimension from the aspect ratio.
    /// </summary>
    private (float Width, float Height) MeasureImageSize(Element el, float availableWidth, float containerRefWidth, float fontSize)
    {
        var computed = GetComputedStyle(el);
        // A wrapper (e.g. <figure>) carries no src of its own: use the first
        // descendant image that does.
        var srcEl = el.GetAttribute("src") is { Length: > 0 } || el.GetAttribute("href") is { Length: > 0 }
            ? el
            : FindImageWithSrc(el) ?? el;

        var imgWidth = float.NaN;
        var imgHeight = float.NaN;

        bool computedWidthParsed = false, computedHeightParsed = false;
        bool attrWidthParsed = false, attrHeightParsed = false;
        float parsedAttrWidth = 0f, parsedAttrHeight = 0f;
        float computedWidth = 0f, computedHeight = 0f;

        var wp = ParseLengthToPx(computed.GetPropertyValue("width"), containerRefWidth, fontSize);
        var hp = ParseLengthToPx(computed.GetPropertyValue("height"), _options.PageHeightPx, fontSize);
        if (wp is > 0) { imgWidth = wp.Value; computedWidthParsed = true; computedWidth = wp.Value; }
        if (hp is > 0) { imgHeight = hp.Value; computedHeightParsed = true; computedHeight = hp.Value; }

        if (srcEl.GetAttribute("width") is { } aw && !string.IsNullOrWhiteSpace(aw))
        {
            if (float.TryParse(aw, NumberStyles.Float, CultureInfo.InvariantCulture, out var aval))
            {
                attrWidthParsed = true;
                parsedAttrWidth = aval;
            }
            else
            {
                var px = ParseLengthToPx(aw, containerRefWidth, fontSize);
                if (px.HasValue) { attrWidthParsed = true; parsedAttrWidth = px.Value; }
            }
        }
        if (srcEl.GetAttribute("height") is { } ah && !string.IsNullOrWhiteSpace(ah))
        {
            if (float.TryParse(ah, NumberStyles.Float, CultureInfo.InvariantCulture, out var aval))
            {
                attrHeightParsed = true;
                parsedAttrHeight = aval;
            }
            else
            {
                var px = ParseLengthToPx(ah, _options.PageHeightPx, fontSize);
                if (px.HasValue) { attrHeightParsed = true; parsedAttrHeight = px.Value; }
            }
        }

        if (float.IsNaN(imgWidth) && attrWidthParsed) imgWidth = parsedAttrWidth;
        if (float.IsNaN(imgHeight) && attrHeightParsed) imgHeight = parsedAttrHeight;

        // Intrinsic image size from cache (a width-only or height-only
        // declaration must keep the image's aspect ratio).
        var intrinsicParsed = false;
        int intrinsicW = 0, intrinsicH = 0;
        if (float.IsNaN(imgWidth) || float.IsNaN(imgHeight))
        {
            try
            {
                var srcAttr = srcEl.GetAttribute("src") ?? srcEl.GetAttribute("href") ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(srcAttr))
                {
                    var clean = srcAttr.Split('#')[0].Split('?')[0].Trim();
                    if (clean.StartsWith("/cache/", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(_coreOptions.CachePath))
                    {
                        var relative = clean["/cache/".Length..].TrimStart('/', '\\');
                        var parts = relative.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
                        var pathParts = new List<string> { _coreOptions.CachePath };
                        pathParts.AddRange(parts);
                        var imagePath = Path.Combine(pathParts.ToArray());
                        if (File.Exists(imagePath))
                        {
                            using var fileStream = File.OpenRead(imagePath);
                            using var stream = new SKManagedStream(fileStream);
                            using var codec = SKCodec.Create(stream);
                            if (codec != null)
                            {
                                intrinsicW = codec.Info.Width;
                                intrinsicH = codec.Info.Height;
                                intrinsicParsed = intrinsicW > 0 && intrinsicH > 0;
                            }
                        }
                    }
                }
            }
            catch { }
        }

        var mw = ParseLengthToPx(computed.GetPropertyValue("max-width"), containerRefWidth, fontSize);
        var maxW = mw is > 0 ? Math.Min(availableWidth, mw.Value) : availableWidth;
        if (imgWidth > maxW) imgWidth = maxW;

        // Derive the missing dimension from the intrinsic aspect ratio
        // (CSS replaced-element model).
        if (intrinsicParsed)
        {
            bool widthMissing = float.IsNaN(imgWidth) || imgWidth <= 0;
            bool heightMissing = float.IsNaN(imgHeight) || imgHeight <= 0;
            if (widthMissing && heightMissing)
            {
                imgWidth = Math.Min(intrinsicW, maxW);
                imgHeight = imgWidth * (intrinsicH / (float)intrinsicW);
            }
            else if (heightMissing)
            {
                imgHeight = imgWidth * (intrinsicH / (float)intrinsicW);
            }
            else if (widthMissing)
            {
                imgWidth = imgHeight * (intrinsicW / (float)intrinsicH);
                if (imgWidth > maxW)
                {
                    imgWidth = maxW;
                    imgHeight = imgWidth * (intrinsicH / (float)intrinsicW);
                }
            }
        }

        if (attrWidthParsed && attrHeightParsed)
        {
            if (parsedAttrWidth > 0) imgHeight = imgWidth * (parsedAttrHeight / parsedAttrWidth);
        }
        else if (computedWidthParsed && computedHeightParsed)
        {
            if (computedWidth > 0)
            {
                if (Math.Abs(imgWidth - computedWidth) > 0.001f) imgHeight = imgWidth * (computedHeight / computedWidth);
                else imgHeight = computedHeight;
            }
        }

        if (float.IsNaN(imgHeight) || imgHeight <= 0)
        {
            if (_options.DefaultImageAspectRatio > 0) imgHeight = imgWidth / _options.DefaultImageAspectRatio;
            else imgHeight = _options.DefaultImageHeightPx;
        }

        var mh = ParseLengthToPx(computed.GetPropertyValue("max-height"), _options.PageHeightPx, fontSize);
        if (mh is > 0 && !float.IsNaN(imgHeight) && imgHeight > mh.Value) imgHeight = mh.Value;

        return (imgWidth, imgHeight);
    }

    /// <summary>
    /// True when the element's computed <c>display</c> is block-like. Images are
    /// inline by UA default; <c>figure</c> is block.
    /// </summary>
    private bool IsBlockDisplay(Element el)
    {
        var display = GetComputedStyle(el).GetPropertyValue("display");
        if (string.IsNullOrWhiteSpace(display))
            return el.TagName.Equals("figure", StringComparison.OrdinalIgnoreCase);
        return display.Trim().ToLowerInvariant() switch
        {
            "inline" or "inline-block" or "inline-flex" or "inline-grid" or "none" => false,
            _ => true
        };
    }

    /// <summary>An image element whose computed display is inline (the UA default).</summary>
    private bool IsInlineImage(Element el) => IsImageElement(el) && !IsBlockDisplay(el);

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
            if (IsImageElement(child) && !IsBlockDisplay(child))
            {
                result.Add(child);
                continue;
            }
            if (!IsBlockLevelTag(child.TagName))
                CollectInlineImagesRecursive(child, null, result);
        }
    }

    /// <summary>First descendant image element with a non-empty src/href, if any.</summary>
    private static Element? FindImageWithSrc(Element el)
    {
        foreach (var child in el.Children)
        {
            if (IsImageElement(child) && (child.GetAttribute("src") is { Length: > 0 } || child.GetAttribute("href") is { Length: > 0 }))
                return child;
            var nested = FindImageWithSrc(child);
            if (nested != null) return nested;
        }
        return null;
    }

    /// <summary>
    /// Parses the <c>line-height</c> keeping the LENGTH and UNITLESS forms separate:
    /// a unitless line-height multiplies each inline's OWN font size (browser model).
    /// </summary>
    private (float? LengthPx, float? Multiplier) ResolveLineHeightForm(MiniStyle computed, float fontSize)
    {
        float? lengthPx = null;
        float? multiplier = null;
        var lh = computed.GetPropertyValue("line-height");
        if (!string.IsNullOrWhiteSpace(lh))
        {
            var lhTrim = lh.Trim();
            if (float.TryParse(lhTrim, NumberStyles.Float, CultureInfo.InvariantCulture, out var lhMul))
                multiplier = lhMul;
            else
            {
                var parsed = ParseLengthToPx(lhTrim, fontSize, fontSize);
                if (parsed.HasValue) lengthPx = parsed.Value;
                else if (CssLengthParser.ParseUnitless(lhTrim) is { } unitless) multiplier = unitless;
            }
        }
        return (lengthPx, multiplier);
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
        // non-collapsible content. The browser collapses space, tab, LF, CR and
        // FF to nothing (a CR from a &#13; reference is a normal line break), but
        // a non-breaking space (&nbsp;, U+00A0) is not collapsible and occupies a
        // line. Any other non-whitespace character (real text) also does.
        if (!HasNonCollapsibleContent(el))
            return (0f, 0, 0f);

        var (lhLengthPx, lhMultiplier) = ResolveLineHeightForm(computed, fontSize);
        var lineHeight = lhLengthPx ?? (lhMultiplier ?? ITextMeasurer.NormalLineHeightMultiplier) * fontSize;
        return (lineHeight, 1, lineHeight);
    }

    private static bool HasNonCollapsibleContent(Element el)
    {
        foreach (var node in el.ChildNodes)
        {
            if (node.NodeType == NodeType.Text)
            {
                if (node.TextContent.Any(c => c is not (' ' or '\t' or '\n' or '\r' or '\f')))
                    return true;
            }
            else if (node is Element childEl && HasNonCollapsibleContent(childEl))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Parses the CSS page-break properties into <see cref="LayoutBlockFlags"/>.</summary>
    private static LayoutBlockFlags ParseBreakFlags(MiniStyle computed)
    {
        LayoutBlockFlags flags = 0;

        // Break-before/after: only the COLUMN value is honored. The page count is
        // derived from a CSS multi-column layout (column-fill: auto), and Chromium
        // treats break-before/after: page (and the legacy page-break-*: always) as
        // print-only — a no-op in columns, where content flows greedily (verified:
        // 5 forced-break divs pack 2 per column, .browser-test/break_test.html).
        // break-before/after: column IS honored (forces a new column,
        // .browser-test/break_test2.html). Honoring the page value would inflate
        // the count (e.g. arcanum ch146: 46 vs the browser's 21).
        var bb = computed.GetPropertyValue("break-before");
        if (!string.IsNullOrWhiteSpace(bb) && bb.Trim().Equals("column", StringComparison.OrdinalIgnoreCase)) flags |= LayoutBlockFlags.PageBreakBefore;

        var ba = computed.GetPropertyValue("break-after");
        if (!string.IsNullOrWhiteSpace(ba) && ba.Trim().Equals("column", StringComparison.OrdinalIgnoreCase)) flags |= LayoutBlockFlags.PageBreakAfter;

        // break-inside: avoid IS honored (Chromium avoids breaking inside the box).
        var pbi = computed.GetPropertyValue("page-break-inside");
        var bi = computed.GetPropertyValue("break-inside");
        if (!string.IsNullOrWhiteSpace(pbi) && pbi.Trim().Equals("avoid", StringComparison.OrdinalIgnoreCase)) flags |= LayoutBlockFlags.AvoidBreakInside;
        if (!string.IsNullOrWhiteSpace(bi) && bi.Trim().Equals("avoid", StringComparison.OrdinalIgnoreCase)) flags |= LayoutBlockFlags.AvoidBreakInside;

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

        var marginTop = TryGetComputedMarginPx(computed, "margin-top", containerRefWidth, fontSize) ?? 0f;
        var marginBottom = TryGetComputedMarginPx(computed, "margin-bottom", containerRefWidth, fontSize) ?? 0f;

        var paddingTop = TryGetComputedPaddingPx(computed, "padding-top", containerRefWidth, fontSize) ?? 0f;
        var paddingBottom = TryGetComputedPaddingPx(computed, "padding-bottom", containerRefWidth, fontSize) ?? 0f;

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

    /// <summary>Extracts text content that belongs directly to the element (not inside block-level children).</summary>
    private static string GetOwnInlineText(Element el, List<Element> blockChildren)
    {
        var sb = new StringBuilder();
        foreach (var node in el.ChildNodes)
        {
            if (node.NodeType == NodeType.Text)
            {
                sb.Append(node.TextContent);
            }
            else if (node is Element childEl && !blockChildren.Contains(childEl))
            {
                // Inline element (span, em, strong, a, etc.) - include its text.
                Element.AppendDescendantText(childEl, sb);
            }
            // Block children are skipped (their text is handled by recursion).
        }

        var start = 0;
        var end = sb.Length;
        while (start < end && char.IsWhiteSpace(sb[start])) start++;
        while (end > start && char.IsWhiteSpace(sb[end - 1])) end--;
        return sb.ToString(start, end - start);
    }

    /// <summary>
    /// Splits the element's inline content into <see cref="TextRun"/>s at their effective
    /// font size and style. Consecutive runs at the same size AND style are merged.
    /// </summary>
    private List<TextRun> BuildTextRuns(Element el, float ownFontSize, bool ownBold, bool ownItalic)
    {
        if (IsUniformInline(el, ownFontSize, ownBold, ownItalic))
        {
            return [new TextRun(el.TextContent, ownFontSize, ToFontStyle(ownBold, ownItalic))];
        }

        var builders = new List<RunBuilder>();
        WalkInlineRuns(el, ownFontSize, ownBold, ownItalic, builders);
        var runs = new List<TextRun>(builders.Count);
        foreach (var b in builders) runs.Add(b.Build());
        return runs;
    }

    /// <summary>
    /// True when every inline descendant renders at the inherited size and style, so the
    /// whole element is a single run and the run walk can be skipped.
    /// </summary>
    private bool IsUniformInline(Element element, float inheritedSize, bool inheritedBold, bool inheritedItalic)
    {
        foreach (var node in element.ChildNodes)
        {
            if (node is not Element childEl || IsBlockLevelTag(childEl.TagName)) continue;

            var childSize = inheritedSize;
            var childBold = inheritedBold;
            var childItalic = inheritedItalic;
            var cs = GetComputedStyle(childEl);
            var fs = cs.GetPropertyValue("font-size");
            if (!string.IsNullOrWhiteSpace(fs) && cs.IsExplicit("font-size"))
            {
                var parsed = ParseLengthToPx(fs, childSize, childSize);
                if (parsed is > 0f) childSize = parsed.Value;
            }
            var fw = cs.GetPropertyValue("font-weight");
            if (!string.IsNullOrWhiteSpace(fw)) childBold = IsBoldWeight(fw);
            var fst = cs.GetPropertyValue("font-style");
            if (!string.IsNullOrWhiteSpace(fst)) childItalic = IsItalicStyle(fst);

            if (Math.Abs(childSize - inheritedSize) >= 0.001f || childBold != inheritedBold || childItalic != inheritedItalic)
                return false;

            if (!IsUniformInline(childEl, childSize, childBold, childItalic))
                return false;
        }

        return true;
    }

    private void WalkInlineRuns(Element element, float inheritedSize, bool inheritedBold, bool inheritedItalic, List<RunBuilder> runs)
    {
        foreach (var node in element.ChildNodes)
        {
            if (node.NodeType == NodeType.Text)
            {
                AppendRun(runs, node.TextContent, inheritedSize, inheritedBold, inheritedItalic);
            }
            else if (node is Element childEl && IsBlockLevelTag(childEl.TagName))
            {
                // Defensive: leaf blocks have no block children; a container's block
                // children become sibling blocks, not part of its own inline text.
            }
            else if (node is Element inline)
            {
                var childSize = inheritedSize;
                var childBold = inheritedBold;
                var childItalic = inheritedItalic;
                var cs = GetComputedStyle(inline);
                var fs = cs.GetPropertyValue("font-size");
                // Only an explicitly declared font-size is re-parsed; an inherited
                // raw string (e.g. "max(2.42em, …)") would apply the em unit twice.
                if (!string.IsNullOrWhiteSpace(fs) && cs.IsExplicit("font-size"))
                {
                    var parsed = ParseLengthToPx(fs, childSize, childSize);
                    if (parsed is > 0f) childSize = parsed.Value;
                }
                var fw = cs.GetPropertyValue("font-weight");
                if (!string.IsNullOrWhiteSpace(fw)) childBold = IsBoldWeight(fw);
                var fst = cs.GetPropertyValue("font-style");
                if (!string.IsNullOrWhiteSpace(fst)) childItalic = IsItalicStyle(fst);
                WalkInlineRuns(inline, childSize, childBold, childItalic, runs);
            }
        }
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

    private static FontStyle ToFontStyle(bool bold, bool italic) =>
        (bold, italic) switch
        {
            (true, true) => FontStyle.BoldItalic,
            (true, false) => FontStyle.Bold,
            (false, true) => FontStyle.Italic,
            _ => FontStyle.Regular
        };

    /// <summary>CSS <c>font-weight</c>: bold at "bold"/"bolder" or a numeric weight ≥ 600.</summary>
    private static bool IsBoldWeight(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var v = value.Trim().ToLowerInvariant();
        if (v is "bold" or "bolder") return true;
        return float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && n >= 600f;
    }

    /// <summary>CSS <c>font-style</c>: italic at "italic"/"oblique".</summary>
    private static bool IsItalicStyle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var v = value.Trim().ToLowerInvariant();
        return v is "italic" or "oblique";
    }

    // The resolver expands every logical property (margin-block, margin-block-start,
    // margin-inline, ...) into physical longhands at cascade time, so the engine reads
    // the physical longhand directly — no per-property fallback chain is needed.
    private float? TryGetComputedMarginPx(MiniStyle computed, string name, float reference, float? emReference)
        => ParseLengthToPx(computed.GetPropertyValue(name), reference, emReference);

    private float? TryGetComputedPaddingPx(MiniStyle computed, string name, float reference, float? emReference)
        => ParseLengthToPx(computed.GetPropertyValue(name), reference, emReference);

    private static bool IsImageElement(Element el)
    {
        return el.TagName.ToUpperInvariant() switch
        {
            "IMG" or "SVG" or "IMAGE" or "FIGURE" or "PICTURE" or "OBJECT" or "CANVAS" => true,
            _ => false
        };
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

    [GeneratedRegex(@"display\s*:\s*none", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex DisplayNoneRegex();
}
