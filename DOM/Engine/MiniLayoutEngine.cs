using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using BookHeaven.Core.DOM.CSS;
using BookHeaven.Core.DOM.Engine.Models;
using BookHeaven.Core.DOM.HTML;
using BookHeaven.Core.DOM.HTML.Models;
using BookHeaven.Core.DOM.Services;
using BookHeaven.Core.DOM.Text.Abstractions;
using BookHeaven.Core.DOM.Text.Measurers;
using Microsoft.Extensions.Options;
using SkiaSharp;
using FontStyle = BookHeaven.Core.DOM.Text.Abstractions.FontStyle;
using MiniLengthParser = BookHeaven.Core.DOM.CSS.MiniLengthParser;

namespace BookHeaven.Core.DOM.Engine;

/// <summary>
/// The block layout engine on top of the lightweight Mini pipeline
/// (<see cref="MiniHtmlParser"/> + <see cref="MiniCssParser"/> +
/// <see cref="MiniStyleResolver"/> + <see cref="MiniLengthParser"/>): the
/// browser-style block model (one <see cref="LayoutBlock"/> per top-level
/// element, explicit margins/paddings, children measured independently for
/// <see cref="BlockPageSplitter"/>), but
/// without AngleSharp — the HTML is parsed in a single pass and the CSS cascade
/// (specificity, source order, !important, inheritance, var()) is resolved
/// in-house.
///
/// The CSS pipeline mirrors the AngleSharp one so results stay comparable: the
/// same global stylesheet is injected, logical margins are expanded to physical
/// longhands before parsing, and chapter stylesheets are appended after it.
/// </summary>
public sealed partial class MiniLayoutEngine : IDisposable
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
    private readonly MiniHtmlParser _parser;

    // Per-document state, cleared per call.
    private readonly Dictionary<MiniElement, MiniStyle> _styleCache = [];
    private readonly Dictionary<MiniElement, bool> _hiddenCache = [];
    private Dictionary<MiniElement, float>? _contentWidthMemo;
    private List<MiniCssRule> _rules = [];

    public MiniLayoutEngine(
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
        _parser = new MiniHtmlParser(_options);

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
        _styleCache.Clear();
        _hiddenCache.Clear();
        _contentWidthMemo = null;

        var (doc, rules) = _parser.Parse(html, css);
        _rules = rules;
        cancellationToken.ThrowIfCancellationRequested();

        var body = doc.Body;
        var blocks = new List<LayoutBlock>();
        if (body == null) return Task.FromResult<IReadOnlyList<LayoutBlock>>(blocks);

        var all = QueryAll(body, BlockSelector);
        if (all.Count == 0)
        {
            var text = body.TextContent.Trim();
            if (string.IsNullOrEmpty(text)) return Task.FromResult<IReadOnlyList<LayoutBlock>>(blocks);

            var block = BuildLeafBlock(text, body);
            if (block != null) blocks.Add(block);
            return Task.FromResult<IReadOnlyList<LayoutBlock>>(blocks);
        }

        var matchedSet = new HashSet<MiniElement>(all);
        var topLevel = all.Where(e => !HasAncestorInSet(e, matchedSet)).ToList();

        foreach (var element in topLevel)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProcessElement(element, blocks, cancellationToken);
        }

        return Task.FromResult<IReadOnlyList<LayoutBlock>>(blocks);
    }

    /// <summary>Collects every element in the subtree (pre-order) whose tag is in the comma-separated selector.</summary>
    private static List<MiniElement> QueryAll(MiniElement root, string selector)
    {
        var tags = selector.Split(',').Select(t => t.Trim().ToLowerInvariant()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new List<MiniElement>();
        Collect(root, result);
        return result;

        void Collect(MiniElement el, List<MiniElement> sink)
        {
            if (tags.Contains(el.TagName)) sink.Add(el);
            foreach (var child in el.Children) Collect(child, sink);
        }
    }

    /// <summary>
    /// Cached computed style: resolved on first use and reused for every lookup of
    /// the same element within the current layout pass.
    /// </summary>
    private MiniStyle GetComputedStyle(MiniElement el, List<MiniCssRule> rules)
    {
        if (_styleCache.TryGetValue(el, out var cached)) return cached;
        var parent = el.ParentElement is { } p ? GetComputedStyle(p, rules) : null;
        var style = MiniStyleResolver.Resolve(el, rules, parent);
        _styleCache[el] = style;
        return style;
    }

    private bool IsHidden(MiniElement el, List<MiniCssRule> rules)
    {
        if (_hiddenCache.TryGetValue(el, out var hidden)) return hidden;
        var result = ComputeHidden(el, rules);
        _hiddenCache[el] = result;
        return result;
    }

    private bool ComputeHidden(MiniElement el, List<MiniCssRule> rules)
    {
        if (el.Attributes.ContainsKey("hidden")) return true;
        var style = el.Style;
        if (!string.IsNullOrWhiteSpace(style) && DisplayNoneRegex().IsMatch(style))
            return true;
        var display = GetComputedStyle(el, rules).GetPropertyValue("display");
        return !string.IsNullOrWhiteSpace(display) && display.Trim().Equals("none", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBlockLevelTag(string tagName) => BlockTags.Contains(tagName);

    private static bool HasAncestorInSet(MiniElement el, HashSet<MiniElement> set)
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
    private float GetContainingBlockContentWidth(MiniElement el, List<MiniCssRule> rules)
    {
        if (el.ParentElement is not { } parent) return _options.PageWidthPx;
        if (_contentWidthMemo is not null && _contentWidthMemo.TryGetValue(parent, out var cached))
            return Math.Min(_options.PageWidthPx, cached > 0 ? cached : _options.PageWidthPx);
        var value = ResolveAncestorContentWidth(parent, rules);
        (_contentWidthMemo ??= []).Add(parent, value);
        return Math.Min(_options.PageWidthPx, value > 0 ? value : _options.PageWidthPx);
    }

    private float ResolveAncestorContentWidth(MiniElement parent, List<MiniCssRule> rules)
    {
        var chain = new List<MiniElement>();
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
            var pc = GetComputedStyle(chain[i], rules);
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
        => MiniLengthParser.ParseLengthToPx(s, _device, reference, emReference);

    /// <summary>
    /// Recursively measures a block element, appending its block to <paramref name="sink"/>.
    /// The engine only measures INTRINSIC geometry (content height, lines, own
    /// margins/paddings, flags, children in document order); it does NOT place
    /// blocks or collapse margins — that is the page splitter's job.
    /// </summary>
    private void ProcessElement(MiniElement el, List<LayoutBlock> sink, CancellationToken ct)
    {
        if (IsHidden(el, _rules)) return;

        // Image elements are always leaves.
        if (IsImageElement(el))
        {
            var imgBlock = BuildLeafBlock(string.Empty, el);
            if (imgBlock != null) sink.Add(imgBlock);
            return;
        }

        var blockChildren = el.Children
            .Where(c => IsBlockLevelTag(c.TagName) && !IsHidden(c, _rules))
            .ToList();
        if (blockChildren.Count == 0)
        {
            // Leaf block: measure its full text content. An EMPTY leaf still
            // produces a block when it occupies space (see BuildLeafBlock).
            var text = el.TextContent.Trim();
            var block = BuildLeafBlock(text, el);
            if (block != null) sink.Add(block);
            return;
        }

        // Container: the element's own vertical properties, as the style sheet
        // declares them. The splitter applies the CSS margin collapse.
        var layout = ComputeVerticalLayout(el);

        var childSink = new List<LayoutBlock>();

        // Measure own inline text (text nodes not inside block children). It is an
        // anonymous box: no margins of its own.
        var ownText = GetOwnInlineText(el, blockChildren);
        if (!string.IsNullOrEmpty(ownText))
        {
            var ownBlock = BuildLeafBlock(ownText, el);
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

        // An empty container (no measured children) occupies space only through an
        // explicit CSS height / <br> lines or its own padding and margins.
        float explicitHeight = 0f;
        if (childSink.Count == 0)
        {
            explicitHeight = ResolveSpacerGeometry(el).ContentHeight;
            if (explicitHeight <= 0f && layout.MarginTop <= 0f && layout.MarginBottom <= 0f
                && layout.PaddingTop <= 0f && layout.PaddingBottom <= 0f)
                return;
        }

        var containerBlock = new LayoutBlock
        {
            TagName = el.TagName,
            Kind = BlockKind.Container,
            ContentHeight = explicitHeight,
            MarginTop = layout.MarginTop,
            MarginBottom = layout.MarginBottom,
            PaddingTop = layout.PaddingTop,
            PaddingBottom = layout.PaddingBottom,
            LineCount = 0,
            LineHeightPx = 0f,
            Links = [],
            Children = childSink,
            Flags = ParseBreakFlags(GetComputedStyle(el, _rules))
        };

        if (childSink.Count > 0)
        {
            if ((childSink[0].Flags & LayoutBlockFlags.PageBreakBefore) != 0) containerBlock.Flags |= LayoutBlockFlags.PageBreakBefore;
            if ((childSink[^1].Flags & LayoutBlockFlags.PageBreakAfter) != 0) containerBlock.Flags |= LayoutBlockFlags.PageBreakAfter;
        }

        sink.Add(containerBlock);
    }

    /// <summary>
    /// Measures a leaf element (text or image) and builds its block with its
    /// INTRINSIC geometry. Returns null when the element is hidden, or when it has
    /// no content AND occupies no space.
    /// </summary>
    private LayoutBlock? BuildLeafBlock(string text, MiniElement el)
    {
        var computed = GetComputedStyle(el, _rules);
        if (IsHidden(el, _rules)) return null;

        var fontSize = ResolveFontSize(el);
        var bold = IsBoldWeight(computed.GetPropertyValue("font-weight"));
        var italic = IsItalicStyle(computed.GetPropertyValue("font-style"));
        var containerRefWidth = GetContainingBlockContentWidth(el, _rules);

        var flags = ParseBreakFlags(computed);

        var marginTop = TryGetComputedMarginPx(computed, "margin-top", containerRefWidth, fontSize) ?? 0f;
        var marginBottom = TryGetComputedMarginPx(computed, "margin-bottom", containerRefWidth, fontSize) ?? 0f;
        var marginLeft = TryGetComputedMarginPx(computed, "margin-left", containerRefWidth, fontSize) ?? 0f;
        var marginRight = TryGetComputedMarginPx(computed, "margin-right", containerRefWidth, fontSize) ?? 0f;

        var paddingTop = TryGetComputedPaddingPx(computed, "padding-top", containerRefWidth, fontSize) ?? 0f;
        var paddingBottom = TryGetComputedPaddingPx(computed, "padding-bottom", containerRefWidth, fontSize) ?? 0f;
        var paddingLeft = TryGetComputedPaddingPx(computed, "padding-left", containerRefWidth, fontSize) ?? 0f;
        var paddingRight = TryGetComputedPaddingPx(computed, "padding-right", containerRefWidth, fontSize) ?? 0f;

        // Empty leaf (no text, not an image): it occupies space only through an
        // explicit CSS height / <br> lines, padding or margins.
        var spacerHeight = 0f;
        var spacerLines = 0;
        var spacerLineHeight = 0f;
        var isSpacerLeaf = string.IsNullOrEmpty(text) && !IsImageElement(el);
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

        var contentHeight = 0f;

        // Image element: compute box and emit single block.
        if (IsImageElement(el))
        {
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

            if (el.GetAttribute("width") is { } aw && !string.IsNullOrWhiteSpace(aw))
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
            if (el.GetAttribute("height") is { } ah && !string.IsNullOrWhiteSpace(ah))
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
                    var srcAttr = el.GetAttribute("src") ?? el.GetAttribute("href") ?? string.Empty;
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

            if (imgWidth > availableWidth) imgWidth = availableWidth;

            // Derive the missing dimension from the intrinsic aspect ratio
            // (CSS replaced-element model).
            if (intrinsicParsed)
            {
                bool widthMissing = float.IsNaN(imgWidth) || imgWidth <= 0;
                bool heightMissing = float.IsNaN(imgHeight) || imgHeight <= 0;
                if (widthMissing && heightMissing)
                {
                    imgWidth = Math.Min(intrinsicW, availableWidth);
                    imgHeight = imgWidth * (intrinsicH / (float)intrinsicW);
                }
                else if (heightMissing)
                {
                    imgHeight = imgWidth * (intrinsicH / (float)intrinsicW);
                }
                else if (widthMissing)
                {
                    imgWidth = imgHeight * (intrinsicW / (float)intrinsicH);
                    if (imgWidth > availableWidth)
                    {
                        imgWidth = availableWidth;
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

        if (string.IsNullOrEmpty(text))
        {
            // Spacer box: its content is the explicit height (or the <br> lines).
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

        var lineHeights = meas.LineHeights;
        for (var li = 0; li < meas.LineCount; li++)
        {
            contentHeight += (lineHeights.Count > 0) ? lineHeights[li] : meas.LineHeightPx;
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
            LineCount = meas.LineCount,
            LineHeightPx = meas.LineHeightPx,
            LineHeights = meas.LineHeights.Count > 0 ? [.. meas.LineHeights] : null,
            Flags = flags,
            Links = []
        };
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
                else if (MiniLengthParser.ParseUnitless(lhTrim) is { } unitless) multiplier = unitless;
            }
        }
        return (lengthPx, multiplier);
    }

    /// <summary>
    /// The space a text-less element occupies as CONTENT: an explicit CSS
    /// <c>height</c> when declared; otherwise one line per <c>&lt;br&gt;</c> child.
    /// </summary>
    private (float ContentHeight, int LineCount, float LineHeightPx) ResolveSpacerGeometry(MiniElement el)
    {
        var computed = GetComputedStyle(el, _rules);
        var fontSize = ResolveFontSize(el);

        var heightPx = ParseLengthToPx(computed.GetPropertyValue("height"), _options.PageHeightPx, fontSize);
        if (heightPx is > 0f) return (heightPx.Value, 0, 0f);

        var lineBreaks = CountDescendants(el, "br");
        if (lineBreaks == 0) return (0f, 0, 0f);

        var (lhLengthPx, lhMultiplier) = ResolveLineHeightForm(computed, fontSize);
        var lineHeight = lhLengthPx ?? (lhMultiplier ?? ITextMeasurer.NormalLineHeightMultiplier) * fontSize;
        return (lineBreaks * lineHeight, lineBreaks, lineHeight);
    }

    private static int CountDescendants(MiniElement root, string tag)
    {
        var count = 0;
        foreach (var child in root.Children)
        {
            if (child.TagName == tag) count++;
            count += CountDescendants(child, tag);
        }
        return count;
    }

    /// <summary>Parses the CSS page-break properties into <see cref="LayoutBlockFlags"/>.</summary>
    private static LayoutBlockFlags ParseBreakFlags(MiniStyle computed)
    {
        LayoutBlockFlags flags = 0;

        var pbb = computed.GetPropertyValue("page-break-before");
        var bb = computed.GetPropertyValue("break-before");
        if (!string.IsNullOrWhiteSpace(pbb) && pbb.Trim().Equals("always", StringComparison.OrdinalIgnoreCase)) flags |= LayoutBlockFlags.PageBreakBefore;
        if (!string.IsNullOrWhiteSpace(bb) && bb.Trim().Equals("always", StringComparison.OrdinalIgnoreCase)) flags |= LayoutBlockFlags.PageBreakBefore;

        var pba = computed.GetPropertyValue("page-break-after");
        var ba = computed.GetPropertyValue("break-after");
        if (!string.IsNullOrWhiteSpace(pba) && pba.Trim().Equals("always", StringComparison.OrdinalIgnoreCase)) flags |= LayoutBlockFlags.PageBreakAfter;
        if (!string.IsNullOrWhiteSpace(ba) && ba.Trim().Equals("always", StringComparison.OrdinalIgnoreCase)) flags |= LayoutBlockFlags.PageBreakAfter;

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
    private (float MarginTop, float MarginBottom, float PaddingTop, float PaddingBottom) ComputeVerticalLayout(MiniElement el)
    {
        var computed = GetComputedStyle(el, _rules);
        var fontSize = ResolveFontSize(el);
        var containerRefWidth = GetContainingBlockContentWidth(el, _rules);

        var marginTop = TryGetComputedMarginPx(computed, "margin-top", containerRefWidth, fontSize) ?? 0f;
        var marginBottom = TryGetComputedMarginPx(computed, "margin-bottom", containerRefWidth, fontSize) ?? 0f;

        var paddingTop = TryGetComputedPaddingPx(computed, "padding-top", containerRefWidth, fontSize) ?? 0f;
        var paddingBottom = TryGetComputedPaddingPx(computed, "padding-bottom", containerRefWidth, fontSize) ?? 0f;

        return (marginTop, marginBottom, paddingTop, paddingBottom);
    }

    private float ResolveFontSize(MiniElement el)
    {
        var computed = GetComputedStyle(el, _rules);
        var fs = computed.GetPropertyValue("font-size");
        if (string.IsNullOrWhiteSpace(fs))
            return _options.RootFontSizePx;

        // em/% in font-size resolve against the parent's RESOLVED font-size.
        var parentFontSize = _options.RootFontSizePx;
        if (el.ParentElement is { } parent)
            parentFontSize = ResolveFontSize(parent);

        var parsed = ParseLengthToPx(fs, parentFontSize, parentFontSize);
        return parsed ?? _options.RootFontSizePx;
    }

    /// <summary>Extracts text content that belongs directly to the element (not inside block-level children).</summary>
    private static string GetOwnInlineText(MiniElement el, List<MiniElement> blockChildren)
    {
        var sb = new StringBuilder();
        foreach (var node in el.ChildNodes)
        {
            if (node.NodeType == MiniNodeType.Text)
            {
                sb.Append(node.TextContent);
            }
            else if (node is MiniElement childEl && !blockChildren.Contains(childEl))
            {
                // Inline element (span, em, strong, a, etc.) - include its text.
                sb.Append(childEl.TextContent);
            }
            // Block children are skipped (their text is handled by recursion).
        }
        return sb.ToString().Trim();
    }

    /// <summary>
    /// Splits the element's inline content into <see cref="TextRun"/>s at their effective
    /// font size and style. Consecutive runs at the same size AND style are merged.
    /// </summary>
    private List<TextRun> BuildTextRuns(MiniElement el, float ownFontSize, bool ownBold, bool ownItalic)
    {
        var builders = new List<RunBuilder>();
        WalkInlineRuns(el, ownFontSize, ownBold, ownItalic, builders);
        if (builders.Count == 0)
        {
            return [new TextRun(el.TextContent.Trim(), ownFontSize, ToFontStyle(ownBold, ownItalic))];
        }
        var runs = new List<TextRun>(builders.Count);
        foreach (var b in builders) runs.Add(b.Build());
        return runs;
    }

    private void WalkInlineRuns(MiniElement element, float inheritedSize, bool inheritedBold, bool inheritedItalic, List<RunBuilder> runs)
    {
        foreach (var node in element.ChildNodes)
        {
            if (node.NodeType == MiniNodeType.Text)
            {
                AppendRun(runs, node.TextContent, inheritedSize, inheritedBold, inheritedItalic);
            }
            else if (node is MiniElement childEl && IsBlockLevelTag(childEl.TagName))
            {
                // Defensive: leaf blocks have no block children; a container's block
                // children become sibling blocks, not part of its own inline text.
            }
            else if (node is MiniElement inline)
            {
                var childSize = inheritedSize;
                var childBold = inheritedBold;
                var childItalic = inheritedItalic;
                var cs = GetComputedStyle(inline, _rules);
                var fs = cs.GetPropertyValue("font-size");
                if (!string.IsNullOrWhiteSpace(fs))
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

    private static bool IsImageElement(MiniElement el)
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
        _styleCache.Clear();
        _hiddenCache.Clear();

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
