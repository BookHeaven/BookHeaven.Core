using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Css.Dom;
using AngleSharp.Css.Values;
using AngleSharp.Dom;
using BookHeaven.Core.Abstractions.Services;
using BookHeaven.Core.DOM.CSS;
using BookHeaven.Core.DOM.Engine.Models;
using BookHeaven.Core.DOM.Html;
using BookHeaven.Core.DOM.Services;
using BookHeaven.Core.DOM.Text.Abstractions;
using BookHeaven.Core.DOM.Text.Measurers;
using BookHeaven.EbookManager;
using Microsoft.Extensions.Options;
using BookHeaven.Core.Entities;
using BookHeaven.Core.Features.Fonts;
using SkiaSharp;
using Mediator;
using FontStyle = BookHeaven.Core.DOM.Text.Abstractions.FontStyle;

namespace BookHeaven.Core.DOM.Engine;

/// <summary>
/// Emits browser-style layout BLOCKS instead of flat render lines: every top-level
/// element (header, paragraph, image, table, ...) produces exactly one
/// <see cref="LayoutBlock"/> whose margin and padding are stored explicitly.
/// The measured children of a container are attached to it as
/// <see cref="LayoutBlock.Children"/> (with their own margins in the
/// container's sub-flow); <see cref="BlockPageSplitter"/> places them individually
/// in the page flow (browser behaviour) instead of treating the container as one box.
/// Paragraphs that span several
/// pages are split at line boundaries by <see cref="BlockPageSplitter"/>.
///
/// Margin model (browser-like, CSS): blocks store their OWN margins exactly as
/// the style sheet computes them. Margins COLLAPSE in the flow: adjacent siblings
/// collapse to the max, and a container without padding lets its first child's
/// top margin and last child's bottom margin collapse through it (see
/// <see cref="LayoutBlock.EffectiveTopMargin"/> / <see cref="LayoutBlock.EffectiveBottomMargin"/>).
/// The engine applies that collapse to compute the flow coordinates;
/// <see cref="BlockPageSplitter"/> re-applies the SAME collapse when placing
/// blocks on pages, so the two can never drift apart.
/// </summary>
public partial class BlockLayoutEngine : IDisposable
{
    private const string BlockSelector = "p,div,h1,h2,h3,h4,h5,h6,li,ul,ol,table,blockquote,pre,section,article,header,footer,main,aside,nav,dl,dt,dd,figure,img,svg,image,picture,object,canvas,hr";

    // Derived from BlockSelector so the two lists can never drift apart
    private static readonly HashSet<string> BlockTags =
        BlockSelector.Split(',').Select(t => t.Trim().ToUpperInvariant()).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private readonly PageCalculatorOptions _options;
    private readonly AngleSharpHtmlParser _parser;
    private readonly ITextMeasurer _measurer;
    private readonly bool _ownsMeasurer;
    private readonly CoreOptions _coreOptions;

    // Per-document caches (same rationale as the old engine's), cleared per call.
    private readonly Dictionary<IElement, ICssStyleDeclaration?> _styleCache = [];
    private readonly Dictionary<IElement, bool> _hiddenCache = [];

    /// <summary>
    /// Per-document style state (style collection, precomputed rule matches, raw
    /// overrides index, content-width memo). Rebuilt for every parsed document and
    /// released in <see cref="Dispose"/>, so each chapter's style state is freed
    /// deterministically with its engine.
    /// </summary>
    private DocumentStyleCache? _docCache;

    /// <summary>
    /// Creates the text measurer implementation selected by the (normalized) options.
    /// Exposed so callers running several layout passes — e.g. chapters of one book
    /// in parallel — can share ONE measurer and keep its word-shaping cache warm.
    /// Without a mediator this path can only use <paramref name="defaultFontDirectory"/>
    /// (one file per style variant); use <see cref="CreateMeasurerAsync"/> to
    /// resolve the per-style variants of <see cref="PageCalculatorOptions.SelectedFont"/>.
    /// </summary>
    public static ITextMeasurer CreateMeasurer(PageCalculatorOptions normalizedOptions, string? defaultFontDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(normalizedOptions);
        return normalizedOptions.TextMeasurer switch
        {
            TextMeasurerType.HarfBuzz => new HarfBuzzTextMeasurer(defaultFontDirectory),
            _ => new NaiveTextMeasurer()
        };
    }

    /// <summary>
    /// Creates the text measurer resolving the per-style font variants (Regular, Italic,
    /// Bold, BoldItalic) of <see cref="PageCalculatorOptions.SelectedFont"/> from the
    /// database: <c>GetAllFonts</c> filtered by family, each font's full path built with
    /// <see cref="IUrlBuilder.FontFilePath"/>. A font whose style/weight is "all" (single
    /// file upload) covers every variant it intersects; the most specific font wins each
    /// variant slot. When <see cref="PageCalculatorOptions.SelectedFont"/> is empty, the
    /// default font directory (<see cref="IUrlBuilder.DefaultFontDirectory"/>) is used.
    /// </summary>
    public static async Task<ITextMeasurer> CreateMeasurerAsync(PageCalculatorOptions normalizedOptions, ISender sender, IUrlBuilder urlBuilder, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(normalizedOptions);
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentNullException.ThrowIfNull(urlBuilder);

        if (normalizedOptions.TextMeasurer != TextMeasurerType.HarfBuzz)
        {
            return new NaiveTextMeasurer();
        }

        if (string.IsNullOrWhiteSpace(normalizedOptions.SelectedFont))
        {
            return CreateMeasurer(normalizedOptions, urlBuilder.DefaultFontDirectory());
        }

        var result = await sender.Send(new GetAllFonts.Query(normalizedOptions.SelectedFont), cancellationToken);
        if (result.IsSuccess)
        {
            var fontPaths = MapFontsToStyles(result.Value, urlBuilder);
            if (fontPaths.Count > 0)
            {
                return new HarfBuzzTextMeasurer(fontPaths);
            }
        }

        // Selected family not found (or query failed): fall back to the default font
        // directory (platform default typeface when it is empty or missing).
        return CreateMeasurer(normalizedOptions, urlBuilder.DefaultFontDirectory());
    }

    /// <summary>
    /// Maps the <see cref="Font"/> rows of one family to the four <see cref="FontStyle"/>
    /// slots. A font covers the intersection of its style set (normal → Regular+Bold,
    /// italic → Italic+BoldItalic, all → everything) and its weight set (normal →
    /// Regular+Italic, bold → Bold+BoldItalic, all → everything); when several fonts
    /// cover the same slot, the most specific one (fewest "all" wildcards) wins.
    /// </summary>
    internal static Dictionary<FontStyle, string?> MapFontsToStyles(IReadOnlyList<Font> fonts, IUrlBuilder urlBuilder)
    {
        ArgumentNullException.ThrowIfNull(urlBuilder);

        var assignments = new List<(FontStyle Style, int Specificity, string Path)>();
        foreach (var font in fonts)
        {
            var style = font.Style.Trim().ToLowerInvariant();
            var weight = font.Weight.Trim().ToLowerInvariant();
            var styleSet = style switch
            {
                "italic" => new[] { FontStyle.Italic, FontStyle.BoldItalic },
                "all" => new[] { FontStyle.Regular, FontStyle.Italic, FontStyle.Bold, FontStyle.BoldItalic },
                _ => new[] { FontStyle.Regular, FontStyle.Bold } // "normal"
            };
            var weightSet = weight switch
            {
                "bold" => new[] { FontStyle.Bold, FontStyle.BoldItalic },
                "all" => new[] { FontStyle.Regular, FontStyle.Italic, FontStyle.Bold, FontStyle.BoldItalic },
                _ => new[] { FontStyle.Regular, FontStyle.Italic } // "normal"
            };
            var specificity = (style != "all" ? 2 : 0) + (weight != "all" ? 1 : 0);
            var path = urlBuilder.FontFilePath(font.Family, font.FileName);
            foreach (var slot in styleSet.Intersect(weightSet))
            {
                assignments.Add((slot, specificity, path));
            }
        }

        // Most specific first; the first assignment wins each slot.
        assignments.Sort((a, b) => b.Specificity.CompareTo(a.Specificity));
        var result = new Dictionary<FontStyle, string?>();
        foreach (var (slot, _, path) in assignments)
        {
            result.TryAdd(slot, path);
        }
        return result;
    }

    public BlockLayoutEngine(
        PageCalculatorOptions options, 
        IOptions<CoreOptions> coreOptions, 
        ITextMeasurer? sharedMeasurer = null)
    {
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Normalize();
        _parser = new AngleSharpHtmlParser(_options);
        ArgumentNullException.ThrowIfNull(coreOptions);
        _coreOptions = coreOptions.Value;

        // A shared measurer (one per book, used by several parallel engines) is not
        // disposed by the engines using it; only the caller that created it owns it.
        _ownsMeasurer = sharedMeasurer == null;
        _measurer = sharedMeasurer ?? CreateMeasurer(_options, _coreOptions.DefaultFontDirectory);
    }

    /// <summary>
    /// Generates a flat list of layout blocks (in flow order) for the given HTML.
    /// </summary>
    public async Task<IReadOnlyList<LayoutBlock>> GenerateLayoutBlocksAsync(string html, IReadOnlyList<string>? css = null, CancellationToken cancellationToken = default)
    {
        _styleCache.Clear();
        _hiddenCache.Clear();

        var doc = await _parser.ParseDocumentAsync(html, css);
        cancellationToken.ThrowIfCancellationRequested();

        // Build the per-document style state once: the style collection (instead of
        // per element) and the inverted rule-match index (instead of testing every
        // rule against every element in the cascade).
        _docCache = new DocumentStyleCache();
        if (doc.Owner is { DefaultView: { } window })
        {
            _docCache.StyleCollection = window.GetStyleCollection(_parser.RenderDevice);
            CssParser.PrecomputeRuleMatches(doc, _docCache.StyleCollection, _docCache);
        }

        var body = doc.Body;
        var blocks = new List<LayoutBlock>();
        if (body == null) return blocks;

        var all = body.QuerySelectorAll(BlockSelector).ToList();
        if (all.Count == 0)
        {
            var text = body.TextContent.Trim();
            if (string.IsNullOrEmpty(text)) return blocks;

            var block = BuildLeafBlock(text, doc, body);
            if (block != null) blocks.Add(block);
            return blocks;
        }

        // Use HashSet for O(1) ancestor lookup instead of O(n) List.Contains
        var matchedSet = new HashSet<IElement>(all);
        var topLevel = all.Where(e => !HasAncestorInSet(e, matchedSet)).ToList();

        foreach (var element in topLevel)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProcessElement(element, doc, blocks, cancellationToken);
        }

        return blocks;
    }

    /// <summary>
    /// Recursively measures a block element, appending its block to <paramref name="sink"/>.
    /// The engine only measures INTRINSIC geometry (content height, lines, own
    /// margins/paddings, flags, links, children in document order); it does NOT place
    /// blocks or collapse margins — that is the page splitter's job.
    /// Leaf elements (no block children, or images) produce one block; containers
    /// produce ONE block whose children are measured independently (the container's
    /// own content height is not intrinsic, so it is left at 0 — the splitter places
    /// the children individually). Elements that contain no text but DO occupy space
    /// (an explicit CSS height, <c>&lt;br&gt;</c> lines, padding or margins) also
    /// produce a block; an empty box that occupies NO space is skipped entirely.
    /// </summary>
    private void ProcessElement(IElement el, IDocument doc, List<LayoutBlock> sink, CancellationToken ct)
    {
        if (IsHidden(el)) return;

        // Image elements are always leaves
        if (IsImageElement(el))
        {
            var imgBlock = BuildLeafBlock(string.Empty, doc, el);
            if (imgBlock != null) sink.Add(imgBlock);
            return;
        }

        var blockChildren = el.Children
            .Where(c => IsBlockLevelTag(c.TagName) && !IsHidden(c))
            .ToList();

        if (blockChildren.Count == 0)
        {
            // Leaf block: measure its full text content. An EMPTY leaf still
            // produces a block when it occupies space (see BuildLeafBlock).
            var text = el.TextContent.Trim();
            var block = BuildLeafBlock(text, doc, el);
            if (block != null) sink.Add(block);
            return;
        }

        // Container: the element's own vertical properties, as the style sheet
        // declares them. The engine stores them as-is; the splitter applies the
        // CSS margin collapse when it places the container and its children.
        var layout = ComputeVerticalLayout(el, doc);

        // Measure the children independently (no sub-flow, no placement).
        var childSink = new List<LayoutBlock>();

        // Measure own inline text (text nodes not inside block children). It is an
        // anonymous box: no margins of its own.
        var ownText = GetOwnInlineText(el, blockChildren);
        if (!string.IsNullOrEmpty(ownText))
        {
            var ownBlock = BuildLeafBlock(ownText, doc, el);
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
            ProcessElement(blockChildren[ci], doc, childSink, ct);
        }

        // An empty container (no measured children) occupies space only through an
        // explicit CSS height / <br> lines or its own padding and margins; a box
        // with none of them is skipped entirely (no block).
        float explicitHeight = 0f;
        if (childSink.Count == 0)
        {
            explicitHeight = ResolveSpacerGeometry(el, doc).ContentHeight;
            if (explicitHeight <= 0f && layout.MarginTop <= 0f && layout.MarginBottom <= 0f
                && layout.PaddingTop <= 0f && layout.PaddingBottom <= 0f)
                return;
        }

        var containerBlock = new LayoutBlock
        {
            TagName = el.TagName,
            Kind = BlockKind.Container,
            // ContentHeight is the intrinsic content height — 0 for a non-empty
            // container (its height is the placed children), the explicit spacer
            // height when empty. The splitter places the block.
            ContentHeight = explicitHeight,
            MarginTop = layout.MarginTop, // own margin; the splitter applies the collapse
            MarginBottom = layout.MarginBottom,
            PaddingTop = layout.PaddingTop,
            PaddingBottom = layout.PaddingBottom,
            LineCount = 0,
            LineHeightPx = 0f,
            Links = [],
            // Children keep their OWN margins; the splitter re-applies the collapse
            // when it places them individually.
            Children = childSink,
            // Own CSS page-break flags + first child's page-break-before +
            // last child's page-break-after (propagated so the splitter can react).
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
    /// Measures a leaf element (text or image) and builds its block with its
    /// INTRINSIC geometry (content height, lines, own margins/paddings, flags,
    /// links). ContentHeight is the intrinsic content height. Returns null when
    /// the element is hidden, or when it has no content
    /// AND occupies no space (no explicit height, no <c>&lt;br&gt;</c>, no padding,
    /// no margins). An empty leaf that DOES occupy space (a spacer: an explicit
    /// CSS height, <c>&lt;br&gt;</c> lines, padding or margins — books use empty
    /// paragraphs this way) emits a block whose content is that spacer geometry;
    /// the splitter places it like any other box.
    /// </summary>
    private LayoutBlock? BuildLeafBlock(string text, IDocument doc, IElement el)
    {
        var computed = GetComputedStyle(el);
        if (computed == null) return null;

        // Ignore elements explicitly hidden
        if (IsHidden(el)) return null;

        var fontSize = ResolveFontSize(el, computed, doc);
        var bold = IsBoldWeight(TryGetProperty(computed, "font-weight"));
        var italic = IsItalicStyle(TryGetProperty(computed, "font-style"));
        var containerRefWidth = GetContainingBlockContentWidth(el, doc);

        // pagination flags
        var flags = ParseBreakFlags(computed);

        // margins / paddings
        var marginTop = TryGetComputedMarginPx(computed, "margin-top", containerRefWidth, fontSize, doc, el) ?? 0f;
        var marginBottom = TryGetComputedMarginPx(computed, "margin-bottom", containerRefWidth, fontSize, doc, el) ?? 0f;
        var marginLeft = TryGetComputedMarginPx(computed, "margin-left", containerRefWidth, fontSize, doc, el) ?? 0f;
        var marginRight = TryGetComputedMarginPx(computed, "margin-right", containerRefWidth, fontSize, doc, el) ?? 0f;

        var paddingTop = TryGetComputedPaddingPx(computed, "padding-top", containerRefWidth, fontSize, doc, el) ?? 0f;
        var paddingBottom = TryGetComputedPaddingPx(computed, "padding-bottom", containerRefWidth, fontSize, doc, el) ?? 0f;
        var paddingLeft = TryGetComputedPaddingPx(computed, "padding-left", containerRefWidth, fontSize, doc, el) ?? 0f;
        var paddingRight = TryGetComputedPaddingPx(computed, "padding-right", containerRefWidth, fontSize, doc, el) ?? 0f;

        // Empty leaf (no text, not an image): it occupies space only through an
        // explicit CSS height / <br> lines, padding or margins (books use empty
        // paragraphs as spacers and a browser renders their box). A box with none
        // of them occupies no space: no block, no flow advance.
        var spacerHeight = 0f;
        var spacerLines = 0;
        var spacerLineHeight = 0f;
        var isSpacerLeaf = string.IsNullOrEmpty(text) && !IsImageElement(el);
        if (isSpacerLeaf)
        {
            (spacerHeight, spacerLines, spacerLineHeight) = ResolveSpacerGeometry(el, doc);
            if (spacerHeight <= 0f && marginTop <= 0f && marginBottom <= 0f
                && paddingTop <= 0f && paddingBottom <= 0f)
                return null;
        }

        // compute available width for measuring
        var availableWidth = Math.Min(_options.PageWidthPx, containerRefWidth);
        var cw = TryGetProperty(computed, "width");
        var cmaxw = TryGetProperty(computed, "max-width");
        var wpx = CssLengthParser.ParseLengthToPx(cw, _parser.RenderDevice, availableWidth, fontSize, doc, el);
        var mwpx = CssLengthParser.ParseLengthToPx(cmaxw, _parser.RenderDevice, availableWidth, fontSize, doc, el);
        if (wpx is > 0) availableWidth = Math.Min(_options.PageWidthPx, wpx.Value);
        else if (mwpx is > 0) availableWidth = Math.Min(_options.PageWidthPx, mwpx.Value);
        else
        {
            // Auto-width block: its width is the containing width minus its own
            // horizontal margins (shrink-to-fit), so the content box to measure
            // against also loses those margins.
            availableWidth -= (marginLeft + marginRight);
        }

        availableWidth -= (paddingLeft + paddingRight);
        if (availableWidth < 0) availableWidth = 0f;

        // compute line-height (see ResolveLineHeightForm for the browser model).
        var (lineHeightLengthPx, lineHeightMultiplier) = ResolveLineHeightForm(computed, fontSize, doc, el);
        var lineHeightPx = lineHeightLengthPx ?? lineHeightMultiplier * fontSize;

        // compute letter-spacing and word-spacing
        float? letterSpacingPx = null;
        var ls = TryGetProperty(computed, "letter-spacing");
        if (!string.IsNullOrWhiteSpace(ls))
        {
            var t = ls.Trim();
            if (t.Equals("normal", StringComparison.OrdinalIgnoreCase)) letterSpacingPx = 0f;
            else
            {
                var parsed = CssLengthParser.ParseLengthToPx(ls, _parser.RenderDevice, containerRefWidth, fontSize, doc, el);
                if (parsed.HasValue) letterSpacingPx = parsed.Value;
            }
        }

        float? wordSpacingPx = null;
        var ws = TryGetProperty(computed, "word-spacing");
        if (!string.IsNullOrWhiteSpace(ws))
        {
            var t = ws.Trim();
            if (t.Equals("normal", StringComparison.OrdinalIgnoreCase)) wordSpacingPx = 0f;
            else
            {
                var parsed = CssLengthParser.ParseLengthToPx(ws, _parser.RenderDevice, containerRefWidth, fontSize, doc, el);
                if (parsed.HasValue) wordSpacingPx = parsed.Value;
            }
        }

        // ContentHeight is the intrinsic content height, accumulated below.
        var contentHeight = 0f;

        // Image element: compute box and emit single block
        if (IsImageElement(el))
        {
            var imgWidth = float.NaN;
            var imgHeight = float.NaN;

            bool computedWidthParsed = false, computedHeightParsed = false;
            bool attrWidthParsed = false, attrHeightParsed = false;
            float parsedAttrWidth = 0f, parsedAttrHeight = 0f;
            float computedWidth = 0f, computedHeight = 0f;

            var wp = CssLengthParser.ParseLengthToPx(TryGetProperty(computed, "width"), _parser.RenderDevice, containerRefWidth, fontSize, doc, el);
            var hp = CssLengthParser.ParseLengthToPx(TryGetProperty(computed, "height"), _parser.RenderDevice, _options.PageHeightPx, fontSize, doc, el);
            if (wp is > 0) { imgWidth = wp.Value; computedWidthParsed = true; computedWidth = wp.Value; }
            if (hp is > 0) { imgHeight = hp.Value; computedHeightParsed = true; computedHeight = hp.Value; }

            if (el.HasAttribute("width"))
            {
                var aw = el.GetAttribute("width");
                if (!string.IsNullOrWhiteSpace(aw))
                {
                    if (float.TryParse(aw, NumberStyles.Float, CultureInfo.InvariantCulture, out float aval))
                    {
                        attrWidthParsed = true;
                        parsedAttrWidth = aval;
                    }
                    else
                    {
                        var px = CssLengthParser.ParseLengthToPx(aw, _parser.RenderDevice, containerRefWidth, fontSize, doc, el);
                        if (px.HasValue) { attrWidthParsed = true; parsedAttrWidth = px.Value; }
                    }
                }
            }
            if (el.HasAttribute("height"))
            {
                var ah = el.GetAttribute("height");
                if (!string.IsNullOrWhiteSpace(ah))
                {
                    if (float.TryParse(ah, NumberStyles.Float, CultureInfo.InvariantCulture, out var aval))
                    {
                        attrHeightParsed = true;
                        parsedAttrHeight = aval;
                    }
                    else
                    {
                        var px = CssLengthParser.ParseLengthToPx(ah, _parser.RenderDevice, _options.PageHeightPx, fontSize, doc, el);
                        if (px.HasValue) { attrHeightParsed = true; parsedAttrHeight = px.Value; }
                    }
                }
            }

            if (float.IsNaN(imgWidth) && attrWidthParsed) imgWidth = parsedAttrWidth;
            if (float.IsNaN(imgHeight) && attrHeightParsed) imgHeight = parsedAttrHeight;

            // Intrinsic image size from cache
            // Load the intrinsic size whenever either dimension is missing (not only when
            // everything is unparsed): a width-only or height-only declaration must keep the
            // image's aspect ratio, which only the intrinsic size provides.
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

            // Derive the missing dimension from the intrinsic aspect ratio (CSS replaced-element
            // model). Which side to derive depends on which one is missing:
            //  * both missing  -> use the intrinsic size, clamped to the available width;
            //  * height missing -> keep the specified width, derive the height from the ratio;
            //  * width missing  -> keep the specified height, derive the width from the ratio
            //                       (clamped to the available width, re-deriving the height).
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

            var mh = CssLengthParser.ParseLengthToPx(TryGetProperty(computed, "max-height"), _parser.RenderDevice, _options.PageHeightPx, fontSize, doc, el);
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

        // text-indent narrows the first line: the line still ends at the content edge,
        // but starts after the indent, so it has less width to work with.
        float? firstLineWidthPx = null;
        var ti = TryGetProperty(computed, "text-indent");
        if (!string.IsNullOrWhiteSpace(ti))
        {
            var indentPx = CssLengthParser.ParseLengthToPx(ti, _parser.RenderDevice, containerRefWidth, fontSize, doc, el);
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

        // Split the inline content into runs at their own font sizes (browser model):
        // an inline child declaring a font size (e.g. a 130% drop-cap span) becomes a run
        // measured at THAT size, and its taller inline box inflates the height of every
        // line it sits on. A single run at the block's own size takes the original
        // Measure path (byte-identical behavior to before).
        var runs = BuildTextRuns(el, fontSize, bold, italic);
        var meas = runs.Count == 1
            ? _measurer.Measure(runs[0].Text, runs[0].FontSizePx, availableWidth,
                lineHeightLengthPx ?? (lineHeightMultiplier is null ? null : lineHeightMultiplier.Value * runs[0].FontSizePx),
                letterSpacingPx, wordSpacingPx, firstLineWidthPx, runs[0].Style)
            : _measurer.MeasureRuns(runs, availableWidth, lineHeightLengthPx, lineHeightMultiplier, letterSpacingPx, wordSpacingPx, firstLineWidthPx);

        // Measure text content: ContentHeight is the sum of the line heights.
        var lineHeights = meas.LineHeights;
        for (var li = 0; li < meas.Lines.Count; li++)
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
            LineCount = meas.Lines.Count,
            LineHeightPx = meas.LineHeightPx,
            LineHeights = meas.LineHeights.Count > 0 ? [.. meas.LineHeights] : null,
            Flags = flags,
            Links = []
        };
    }

    /// <summary>
    /// Parses the computed <c>line-height</c> keeping the LENGTH and UNITLESS forms
    /// separate: a unitless line-height multiplies each inline's OWN font size
    /// (browser model), so a block with mixed-size runs needs the multiplier, not a
    /// pre-resolved px value. A % line-height resolves against the element's OWN font
    /// size (CSS spec) — not the containing block's width.
    /// </summary>
    private (float? LengthPx, float? Multiplier) ResolveLineHeightForm(ICssStyleDeclaration? computed, float fontSize, IDocument? doc, IElement? el)
    {
        float? lengthPx = null;
        float? multiplier = null;
        var lh = TryGetProperty(computed, "line-height");
        if (!string.IsNullOrWhiteSpace(lh))
        {
            var lhTrim = lh.Trim();
            if (float.TryParse(lhTrim, NumberStyles.Float, CultureInfo.InvariantCulture, out var lhMul))
                multiplier = lhMul;
            else
            {
                var parsed = CssLengthParser.ParseLengthToPx(lhTrim, _parser.RenderDevice, fontSize, fontSize, doc, el);
                if (parsed.HasValue) lengthPx = parsed.Value;
            }
        }

        return (lengthPx, multiplier);
    }

    /// <summary>
    /// The space a text-less element occupies as CONTENT: an explicit CSS
    /// <c>height</c> when declared; otherwise one line per <c>&lt;br&gt;</c> child
    /// (a browser renders <c>&lt;p&gt;&lt;br&gt;&lt;/p&gt;</c> as a one-line box —
    /// a classic book spacer). Zero when the box has neither; the element then
    /// occupies space only through its padding or margins.
    /// </summary>
    private (float ContentHeight, int LineCount, float LineHeightPx) ResolveSpacerGeometry(IElement el, IDocument doc)
    {
        var computed = GetComputedStyle(el);
        if (computed == null) return (0f, 0, 0f);
        var fontSize = ResolveFontSize(el, computed, doc);

        var heightPx = CssLengthParser.ParseLengthToPx(TryGetProperty(computed, "height"), _parser.RenderDevice, _options.PageHeightPx, fontSize, doc, el);
        if (heightPx is > 0f) return (heightPx.Value, 0, 0f);

        var lineBreaks = el.QuerySelectorAll("br").Length;
        if (lineBreaks == 0) return (0f, 0, 0f);

        var (lhLengthPx, lhMultiplier) = ResolveLineHeightForm(computed, fontSize, doc, el);
        // No line-height declared: the measurers approximate "normal" with the
        // shared ITextMeasurer factor, so the spacer does too.
        var lineHeight = lhLengthPx ?? (lhMultiplier ?? ITextMeasurer.NormalLineHeightMultiplier) * fontSize;
        return (lineBreaks * lineHeight, lineBreaks, lineHeight);
    }

    /// <summary>
    /// Parses the CSS page-break properties into <see cref="LayoutBlockFlags"/>.
    /// Same strict "always"/"avoid" matching as the old engine.
    /// </summary>
    private static LayoutBlockFlags ParseBreakFlags(ICssStyleDeclaration? computed)
    {
        LayoutBlockFlags flags = 0;
        if (computed == null) return flags;

        var pbb = TryGetProperty(computed, "page-break-before");
        var bb = TryGetProperty(computed, "break-before");
        if (!string.IsNullOrWhiteSpace(pbb) && pbb.Trim().Equals("always", StringComparison.OrdinalIgnoreCase)) flags |= LayoutBlockFlags.PageBreakBefore;
        if (!string.IsNullOrWhiteSpace(bb) && bb.Trim().Equals("always", StringComparison.OrdinalIgnoreCase)) flags |= LayoutBlockFlags.PageBreakBefore;

        var pba = TryGetProperty(computed, "page-break-after");
        var ba = TryGetProperty(computed, "break-after");
        if (!string.IsNullOrWhiteSpace(pba) && pba.Trim().Equals("always", StringComparison.OrdinalIgnoreCase)) flags |= LayoutBlockFlags.PageBreakAfter;
        if (!string.IsNullOrWhiteSpace(ba) && ba.Trim().Equals("always", StringComparison.OrdinalIgnoreCase)) flags |= LayoutBlockFlags.PageBreakAfter;

        var pbi = TryGetProperty(computed, "page-break-inside");
        var bi = TryGetProperty(computed, "break-inside");
        if (!string.IsNullOrWhiteSpace(pbi) && pbi.Trim().Equals("avoid", StringComparison.OrdinalIgnoreCase)) flags |= LayoutBlockFlags.AvoidBreakInside;
        if (!string.IsNullOrWhiteSpace(bi) && bi.Trim().Equals("avoid", StringComparison.OrdinalIgnoreCase)) flags |= LayoutBlockFlags.AvoidBreakInside;

        return flags;
    }


    /// <summary>
    /// Computes the element's own vertical layout properties exactly as the style
    /// sheet declares them (like getComputedStyle). The flow applies the CSS margin
    /// collapse on top of these own values (see <see cref="ProcessElement"/>).
    /// </summary>
    private (float MarginTop, float MarginBottom, float PaddingTop, float PaddingBottom) ComputeVerticalLayout(IElement el, IDocument doc)
    {
        var computed = GetComputedStyle(el);
        if (computed == null) return (0f, 0f, 0f, 0f);

        var fontSize = ResolveFontSize(el, computed, doc);
        var containerRefWidth = GetContainingBlockContentWidth(el, doc);

        var marginTop = TryGetComputedMarginPx(computed, "margin-top", containerRefWidth, fontSize, doc, el) ?? 0f;
        var marginBottom = TryGetComputedMarginPx(computed, "margin-bottom", containerRefWidth, fontSize, doc, el) ?? 0f;

        var paddingTop = TryGetComputedPaddingPx(computed, "padding-top", containerRefWidth, fontSize, doc, el) ?? 0f;
        var paddingBottom = TryGetComputedPaddingPx(computed, "padding-bottom", containerRefWidth, fontSize, doc, el) ?? 0f;

        return (marginTop, marginBottom, paddingTop, paddingBottom);
    }

    private float ResolveFontSize(IElement el, ICssStyleDeclaration? computed, IDocument doc)
    {
        var fontSize = _options.RootFontSizePx;
        var fs = TryGetProperty(computed, "font-size");
        if (!string.IsNullOrWhiteSpace(fs))
        {
            var parentFontSize = _options.RootFontSizePx;
            try
            {
                if (el.ParentElement != null)
                {
                    var parentComp = GetComputedStyle(el.ParentElement);
                    var pParsed = TryGetComputedPx(parentComp, "font-size", _options.PageWidthPx, _options.RootFontSizePx, doc, el.ParentElement);
                    if (pParsed.HasValue) parentFontSize = pParsed.Value;
                }
            }
            catch { }

            // % font-sizes resolve against the parent's font size, never the page width.
            var parsed = CssLengthParser.ParseLengthToPx(fs, _parser.RenderDevice, parentFontSize, parentFontSize, doc, el);
            if (parsed.HasValue) fontSize = parsed.Value;
        }
        return fontSize;
    }

    /// <summary>
    /// Extracts text content that belongs directly to the element (not inside block-level children).
    /// </summary>
    private static string GetOwnInlineText(IElement el, List<IElement> blockChildren)
    {
        var sb = new StringBuilder();
        foreach (var node in el.ChildNodes)
        {
            if (node.NodeType == NodeType.Text)
            {
                sb.Append(node.TextContent);
            }
            else if (node is IElement childEl && !blockChildren.Contains(childEl))
            {
                // Inline element (span, em, strong, a, etc.) - include its text
                sb.Append(childEl.TextContent);
            }
            // Block children are skipped (their text is handled by recursion)
        }
        return sb.ToString().Trim();
    }

    /// <summary>
    /// Splits the element's inline content into <see cref="TextRun"/>s at their effective
    /// font size and style. Inline children that declare their own font size, weight or
    /// style (e.g. a 130% drop-cap <c>&lt;span class="v"&gt;P&lt;/span&gt;</c>) become runs at
    /// THAT size/style; text inherits the size, weight and style of its nearest inline
    /// ancestor (percent/em resolve against the parent chain, mirroring the CSS cascade).
    /// Consecutive runs at the same size AND style are merged. When every run merges into
    /// one, its text is exactly the trimmed full text, so callers take the original
    /// single-<see cref="ITextMeasurer.Measure"/> path byte-identically.
    /// </summary>
    private List<TextRun> BuildTextRuns(IElement el, float ownFontSize, bool ownBold, bool ownItalic)
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

    private void WalkInlineRuns(IElement element, float inheritedSize, bool inheritedBold, bool inheritedItalic, List<RunBuilder> runs)
    {
        foreach (var node in element.ChildNodes)
        {
            if (node.NodeType == NodeType.Text)
            {
                AppendRun(runs, node.TextContent, inheritedSize, inheritedBold, inheritedItalic);
            }
            else if (node is IElement childEl && IsBlockLevelTag(childEl.TagName))
            {
                // Defensive: leaf blocks have no block children; a container's block
                // children become sibling blocks, not part of its own inline text.
            }
            else if (node is IElement inline)
            {
                var childSize = inheritedSize;
                var childBold = inheritedBold;
                var childItalic = inheritedItalic;
                var cs = GetComputedStyle(inline);
                var fs = TryGetProperty(cs, "font-size");
                if (!string.IsNullOrWhiteSpace(fs))
                {
                    var parsed = CssLengthParser.ParseLengthToPx(fs, _parser.RenderDevice, childSize, childSize, doc: null, el: inline);
                    if (parsed is > 0f) childSize = parsed.Value;
                }
                var fw = TryGetProperty(cs, "font-weight");
                if (!string.IsNullOrWhiteSpace(fw)) childBold = IsBoldWeight(fw);
                var fst = TryGetProperty(cs, "font-style");
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
    /// Accumulates the text of a single run in a <see cref="StringBuilder"/> so that merging
    /// consecutive same-style fragments is O(total text) instead of the O(n²) string
    /// re-concatenation the old <c>runs[^1] with { Text = ... }</c> performed. The final
    /// <see cref="TextRun"/> is materialized once, at the end of the walk.
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

    private float? TryGetComputedPx(ICssStyleDeclaration? computed, string name, float reference, float? emReference, IDocument? doc, IElement? el)
    {
        if (computed == null) return null;
        try
        {
            var val = computed.GetPropertyValue(name);
            return CssLengthParser.ParseLengthToPx(val, _parser.RenderDevice, reference, emReference, doc, el);
        }
        catch { return null; }
    }
    
    private float? TryGetComputedMarginPx(ICssStyleDeclaration? computed, string name, float reference, float? emReference, IDocument? doc, IElement? el)
    {
        if (computed == null) return null;
        try
        {
            switch (name)
            {
                case "margin-top":
                    return CssLengthParser.ParseLengthToPx(computed.GetPropertyValue("margin-top"), _parser.RenderDevice, reference, emReference, doc, el)  ??
                           CssLengthParser.ParseLengthToPx(computed.GetPropertyValue("margin-block"), _parser.RenderDevice, reference, emReference, doc, el);
                case "margin-bottom":
                    return CssLengthParser.ParseLengthToPx(computed.GetPropertyValue("margin-bottom"), _parser.RenderDevice, reference, emReference, doc, el) ??
                           CssLengthParser.ParseLengthToPx(computed.GetPropertyValue("margin-block"), _parser.RenderDevice, reference, emReference, doc, el);
                case "margin-left":
                    return CssLengthParser.ParseLengthToPx(computed.GetPropertyValue("margin-left"), _parser.RenderDevice, reference, emReference, doc, el) ??
                           CssLengthParser.ParseLengthToPx(computed.GetPropertyValue("margin-inline"), _parser.RenderDevice, reference, emReference, doc, el);
                case "margin-right":
                    return CssLengthParser.ParseLengthToPx(computed.GetPropertyValue("margin-right"), _parser.RenderDevice, reference, emReference, doc, el) ??
                           CssLengthParser.ParseLengthToPx(computed.GetPropertyValue("margin-inline"), _parser.RenderDevice, reference, emReference, doc, el);
            }
        }
        catch { }
        return null;
    }
    
    private float? TryGetComputedPaddingPx(ICssStyleDeclaration? computed, string name, float reference, float? emReference, IDocument? doc, IElement? el)
    {
        if (computed == null) return null;
        try
        {
            switch (name)
            {
                case "padding-top":
                    return CssLengthParser.ParseLengthToPx(computed.GetPropertyValue("padding-top"), _parser.RenderDevice, reference, emReference, doc, el) ??
                           CssLengthParser.ParseLengthToPx(computed.GetPropertyValue("padding-block-start"), _parser.RenderDevice, reference, emReference, doc, el) ??
                           CssLengthParser.ParseLengthToPx(computed.GetPropertyValue("padding-block"), _parser.RenderDevice, reference, emReference, doc, el);
                case "padding-bottom":
                    return CssLengthParser.ParseLengthToPx(computed.GetPropertyValue("padding-bottom"), _parser.RenderDevice, reference, emReference, doc, el) ??
                           CssLengthParser.ParseLengthToPx(computed.GetPropertyValue("padding-block-end"), _parser.RenderDevice, reference, emReference, doc, el) ??
                           CssLengthParser.ParseLengthToPx(computed.GetPropertyValue("padding-block"), _parser.RenderDevice, reference, emReference, doc, el);
                case "padding-left":
                    return CssLengthParser.ParseLengthToPx(computed.GetPropertyValue("padding-left"), _parser.RenderDevice, reference, emReference, doc, el) ??
                           CssLengthParser.ParseLengthToPx(computed.GetPropertyValue("padding-inline-start"), _parser.RenderDevice, reference, emReference, doc, el) ??
                           CssLengthParser.ParseLengthToPx(computed.GetPropertyValue("padding-inline"), _parser.RenderDevice, reference, emReference, doc, el);
                case "padding-right":
                    return CssLengthParser.ParseLengthToPx(computed.GetPropertyValue("padding-right"), _parser.RenderDevice, reference, emReference, doc, el) ??
                           CssLengthParser.ParseLengthToPx(computed.GetPropertyValue("padding-inline-end"), _parser.RenderDevice, reference, emReference, doc, el) ??
                           CssLengthParser.ParseLengthToPx(computed.GetPropertyValue("padding-inline"), _parser.RenderDevice, reference, emReference, doc, el);
            }
        }
        catch { }
        return null;
    }

    private float? ParseLengthToPx(string? s, float reference, float? emReference = null, IDocument? doc = null, IElement? el = null)
    {
        return CssLengthParser.ParseLengthToPx(s, _parser.RenderDevice, reference, emReference, doc, el);
    }

    private static string TryGetProperty(ICssStyleDeclaration? computed, string name)
    {
        if (computed == null) return string.Empty;
        try
        {
            return computed.GetPropertyValue(name);
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Computes an element's style without ever throwing. AngleSharp's ComputeStyle
    /// aborts the whole element when any single property fails (e.g. line-height in
    /// em units with an unusable render device), so on failure the cascaded style is
    /// computed property by property: properties that still compute keep their values
    /// and the failing ones keep their raw values, which the engine's length parser
    /// turns into its defaults.
    /// Cached accessor: computes the style on first use and reuses it for every
    /// subsequent lookup of the same element within the current layout pass.
    /// </summary>
    private ICssStyleDeclaration? GetComputedStyle(IElement el)
    {
        if (_styleCache.TryGetValue(el, out var cached)) return cached;
        var computed = CssParser.ComputeStyle(el, _parser.RenderDevice, el.ParentElement is { } parent ? GetComputedStyle(parent) : null, _docCache);
        _styleCache.Add(el, computed);
        return computed;
    }

    /// <summary>
    /// Compute context for the per-property fallback, mirroring AngleSharp's internal
    /// context; custom properties resolve against the cascaded style itself.
    /// </summary>
    private sealed class TolerantComputeContext(IRenderDevice device, IBrowsingContext? context, ICssProperties properties)
        : ICssComputeContext
    {
        public IRenderDevice Device { get; } = device;
        public IBrowsingContext? Context { get; } = context;
        public IValueConverter? Converter => null;
        public ICssValue? Resolve(string name) => properties.GetProperty(name)?.RawValue;
    }

    private bool IsHidden(IElement el)
    {
        if (_hiddenCache.TryGetValue(el, out var hidden)) return hidden;
        var result = ComputeHidden(el);
        _hiddenCache.Add(el, result);
        return result;
    }

    private bool ComputeHidden(IElement el)
    {
        if (el.HasAttribute("hidden")) return true;
        var style = el.GetAttribute("style");
        if (!string.IsNullOrWhiteSpace(style) && DisplayNoneRegex().IsMatch(style))
            return true;
        var display = TryGetProperty(GetComputedStyle(el), "display");
        return !string.IsNullOrWhiteSpace(display) && display.Trim().Equals("none", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBlockLevelTag(string tagName) => BlockTags.Contains(tagName);

    private static bool HasAncestorInSet(IElement el, HashSet<IElement> set)
    {
        var p = el.ParentElement;
        while (p != null)
        {
            if (set.Contains(p)) return true;
            p = p.ParentElement;
        }
        return false;
    }

    // Resolved content width of an element, per document: it depends only on the
    // element and its ancestor chain, so siblings (and repeated lookups of the same
    // parent from first/last-child width resolution) reuse one computation. The memo
    // lives in the per-document cache so it is freed with the engine.
    private float GetContainingBlockContentWidth(IElement el, IDocument doc)
    {
        if (el.ParentElement is not { } parent) return _options.PageWidthPx;
        var memo = _docCache?.ContentWidth;
        if (memo is not null && memo.TryGetValue(parent, out var cached))
            return Math.Min(_options.PageWidthPx, cached > 0 ? cached : _options.PageWidthPx);
        var value = ResolveAncestorContentWidth(parent);
        if (memo is not null)
            memo[parent] = value;
        return Math.Min(_options.PageWidthPx, value > 0 ? value : _options.PageWidthPx);
    }

    private float ResolveAncestorContentWidth(IElement parent)
    {
        var chain = new List<IElement>();
        for (var p = parent; p != null; p = p.ParentElement) chain.Add(p);

        // The containing block of a static element is its parent's content box. Walk
        // the parent chain top-down so each ancestor's % width/margin/padding resolves
        // against ITS containing block (not the page): an ancestor with an explicit
        // width uses it, an auto-width ancestor is narrowed by its own horizontal
        // margins (CSS 2.1 shrink-to-fit) before its padding is removed. The result
        // is the nearest ancestor with a usable content width, mirroring the old
        // walk-up-until-positive behaviour.
        float cbWidth = _options.PageWidthPx;
        var result = -1f;
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            try
            {
                var pc = GetComputedStyle(chain[i]);
                if (pc == null) continue;

                var pl = ParseLengthToPx(TryGetProperty(pc, "padding-left"), cbWidth) ?? 0f;
                var pr = ParseLengthToPx(TryGetProperty(pc, "padding-right"), cbWidth) ?? 0f;
                var ml = ParseLengthToPx(TryGetProperty(pc, "margin-left"), cbWidth) ?? 0f;
                var mr = ParseLengthToPx(TryGetProperty(pc, "margin-right"), cbWidth) ?? 0f;
                var pw = ParseLengthToPx(TryGetProperty(pc, "width"), cbWidth);
                var boxWidth = pw ?? (cbWidth - ml - mr);
                var contentW = boxWidth - pl - pr;

                cbWidth = contentW;
                if (contentW > 0) result = contentW;
            }
            catch { }
        }
        return result;
    }

    private static bool IsImageElement(IElement el)
    {
        return el.TagName.ToUpperInvariant() switch
        {
            "IMG" or "SVG" or "IMAGE" or "FIGURE" or "PICTURE" or "OBJECT" or "CANVAS" => true,
            _ => false
        };
    }

    public void Dispose()
    {
        // Release the per-document style state deterministically (it references the
        // parsed DOM, which would otherwise pin the whole chapter until the
        // ConditionalWeakTable entries were finalized).
        _docCache = null;
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

    [GeneratedRegex(@"display\s*:\s*none", RegexOptions.IgnoreCase | RegexOptions.Compiled, "es-ES")]
    private static partial Regex DisplayNoneRegex();
}
