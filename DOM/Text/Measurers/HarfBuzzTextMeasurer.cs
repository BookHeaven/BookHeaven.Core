using System.Collections.Concurrent;
using System.Text;
using BookHeaven.Core.DOM.Text.Abstractions;
using HarfBuzzSharp;
using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace BookHeaven.Core.DOM.Text.Measurers;

/// <summary>
/// <see cref="ITextMeasurer"/> backed by HarfBuzz text shaping, with ONE typeface per
/// <see cref="FontStyle"/> variant (Regular, Italic, Bold, BoldItalic) — the same model
/// a browser uses to pick a face from a family.
///
/// Each unique word is shaped ONCE per style at the font's native scale (its
/// unitsPerEm): at that scale HarfBuzz advances are EXACT font units (no rounding),
/// which is the same convention browsers use — shaping at any other scale rounds each
/// glyph advance and the error accumulates across a line (enough to flip a line break).
/// Shaping is font-size independent, so the cached advance sum is rescaled per font
/// size with a multiply instead of reshaping.
///
/// The measurer is THREAD-SAFE, so a single instance can be shared by several
/// layout threads (see <see cref="PageCalculator"/>):
///  * The word caches are concurrent dictionaries — the shaping cost is paid
///    once per unique word for the whole book no matter how chapters are scheduled.
///  * The HarfBuzz font is immutable once created (HarfBuzz contract), so each
///    thread shapes on its OWN buffer (ThreadLocal) and shaping runs in PARALLEL
///    with no lock.
///  * Only the legacy SKShaper/SKFont fallback (used when HarfBuzz shaping is
///    unavailable) is not thread-safe; that path is serialized with a lock.
/// </summary>
public class HarfBuzzTextMeasurer : ITextMeasurer, IDisposable
{
    // Per-style fallback chains: a missing variant degrades to the closest available
    // face (a browser synthesizes oblique/bold; we approximate with the nearest real face).
    private static readonly FontStyle[][] FallbackChains =
    [
        [FontStyle.Regular],
        [FontStyle.Italic, FontStyle.Regular],
        [FontStyle.Bold, FontStyle.Regular],
        [FontStyle.BoldItalic, FontStyle.Italic, FontStyle.Bold, FontStyle.Regular]
    ];

    // One shaper per style; styles that resolve to the SAME typeface share one shaper.
    private readonly StyleShaper[] _shapers;
    // One buffer per shaping thread: the shared HBFonts are immutable (safe for
    // concurrent shaping), so threads shape in parallel on their own buffers.
    private readonly ThreadLocal<HarfBuzzSharp.Buffer> _bufferPerThread = new(() => new HarfBuzzSharp.Buffer());
    // Serializes the legacy SKShaper/SKFont fallback only (stateful, not
    // thread-safe); the HarfBuzz path above takes no lock at all.
    private readonly Lock _shapeLock = new();
    private readonly ConcurrentDictionary<(string Word, float FontSize, FontStyle Style), float> _wordWidthCache = [];
    private readonly ConcurrentDictionary<(string Text, FontStyle Style), float> _wordAdvanceSumCache = [];

    /// <summary>
    /// Creates a measurer with one font file per style variant. Missing or
    /// unresolvable entries fall back through the style's fallback chain and,
    /// ultimately, to the platform default typeface.
    /// </summary>
    public HarfBuzzTextMeasurer(IReadOnlyDictionary<FontStyle, string?> fontPaths)
    {
        ArgumentNullException.ThrowIfNull(fontPaths);

        var resolved = new SKTypeface[Enum.GetValues<FontStyle>().Length];
        for (var i = 0; i < resolved.Length; i++)
        {
            var style = (FontStyle)i;
            resolved[i] = ResolveTypeface(style, fontPaths);
        }

        // Dedupe: styles that resolved to the same typeface share one shaper (and its
        // per-size SKFont cache), so a family shipped as a single file costs one face.
        var unique = new List<StyleShaper>();
        _shapers = new StyleShaper[resolved.Length];
        for (var i = 0; i < resolved.Length; i++)
        {
            var typeface = resolved[i];
            var shaper = unique.FirstOrDefault(s => ReferenceEquals(s.Typeface, typeface));
            if (shaper == null)
            {
                shaper = new StyleShaper(typeface);
                unique.Add(shaper);
            }
            _shapers[i] = shaper;
        }
    }

    /// <summary>
    /// Convenience overload: one font file used for ALL style variants (or the
    /// platform default typeface when <paramref name="fontPath"/> is null).
    /// </summary>
    public HarfBuzzTextMeasurer(string? fontPath = null)
        : this(fontPath == null
            ? []
            : new Dictionary<FontStyle, string?>
            {
                [FontStyle.Regular] = fontPath,
                [FontStyle.Italic] = fontPath,
                [FontStyle.Bold] = fontPath,
                [FontStyle.BoldItalic] = fontPath
            })
    {
    }

    private static SKTypeface ResolveTypeface(FontStyle style, IReadOnlyDictionary<FontStyle, string?> fontPaths)
    {
        foreach (var candidate in FallbackChains[(int)style])
        {
            if (!fontPaths.TryGetValue(candidate, out var path) || string.IsNullOrWhiteSpace(path)) continue;
            if (!File.Exists(path))
            {
                Console.WriteLine($"[HarfBuzzTextMeasurer] Font file not found: '{path}' (style {candidate}). Trying next fallback.");
                continue;
            }
            try
            {
                var typeface = SKTypeface.FromFile(path);
                if (typeface != null) return typeface;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HarfBuzzTextMeasurer] Exception while loading typeface '{path}': {ex.Message}. Trying next fallback.");
            }
        }
        return SKTypeface.Default;
    }

    public TextMeasurementResult Measure(
        string text,
        float fontSizePx,
        float maxWidthPx,
        float? lineHeightPx = null,
        float? letterSpacingPx = null,
        float? wordSpacingPx = null,
        float? firstLineWidthPx = null,
        FontStyle style = FontStyle.Regular)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(fontSizePx, 0f);
        var lineHeight = lineHeightPx ?? (fontSizePx * ITextMeasurer.NormalLineHeightMultiplier);
        var lines = new List<string>();
        if (string.IsNullOrEmpty(text))
        {
            return new TextMeasurementResult
            {
                Lines = lines,
                LineHeightPx = lineHeight,
                TotalHeightPx = 0f
            };
        }
        if (maxWidthPx <= 0f)
        {
            lines.Add(text);
            return new TextMeasurementResult
            {
                Lines = lines,
                LineHeightPx = lineHeight,
                TotalHeightPx = lineHeight * lines.Count
            };
        }
        // Word widths come from a per-(word, fontSize, style) cache: running text
        // repeats words constantly, so only the first occurrence of each word is
        // shaped with HarfBuzz (the expensive part).
        // The current line is built with a StringBuilder (the old `current + " " + w`
        // was O(n^2) and allocated a new string per word); the produced line text
        // is byte-identical to the string-concat version.
        var spaceWidth = GetWordWidth(" ", fontSizePx, style);
        if (wordSpacingPx.HasValue)
        {
            spaceWidth += wordSpacingPx.Value;
        }

        var sb = new StringBuilder(text.Length);
        var currentWidth = 0f;

        foreach (var w in TextRunWrapper.CollapsibleWhitespace().Split(text))
        {
            if (w.Length == 0)
            {
                // Leading/trailing whitespace: a browser collapses and drops it.
                continue;
            }
            var limit = ((lines.Count == 0 && firstLineWidthPx.HasValue) ? firstLineWidthPx.Value : maxWidthPx);
            var wWidth = GetWordWidth(w, fontSizePx, style);
            if (letterSpacingPx.HasValue && w.Length > 1)
            {
                wWidth += (w.Length - 1) * letterSpacingPx.Value;
            }
            var tokenWidth = (sb.Length == 0 ? wWidth : (currentWidth + spaceWidth + wWidth));
            if (tokenWidth <= limit || sb.Length == 0)
            {
                if (sb.Length > 0)
                {
                    sb.Append(' ');
                }
                sb.Append(w);
                currentWidth = tokenWidth;
            }
            else
            {
                // The token doesn't fit on the current line. A browser may break
                // WITHIN a token at a dash (em/en dash, etc. — UAX #14 break
                // opportunities): it keeps the part before the dash on this line and
                // pushes the dash + remainder to the next. Without this, a word like
                // "oeste—." is atomic and wraps whole, adding a line the browser
                // doesn't have.
                var dashIndex = FindBreakableDash(w, currentWidth, spaceWidth, limit, fontSizePx, style, letterSpacingPx);
                if (dashIndex > 0)
                {
                    var head = w.AsSpan(0, dashIndex).ToString();
                    var tail = w.AsSpan(dashIndex).ToString();
                    var headWidth = GetWordWidth(head, fontSizePx, style);
                    if (letterSpacingPx.HasValue && head.Length > 1)
                        headWidth += (head.Length - 1) * letterSpacingPx.Value;
                    sb.Append(' ').Append(head);
                    currentWidth += spaceWidth + headWidth;
                    lines.Add(sb.ToString());
                    sb.Clear();
                    sb.Append(tail);
                    currentWidth = GetWordWidth(tail, fontSizePx, style);
                    if (letterSpacingPx.HasValue && tail.Length > 1)
                        currentWidth += (tail.Length - 1) * letterSpacingPx.Value;
                }
                else
                {
                    lines.Add(sb.ToString());
                    sb.Clear();
                    sb.Append(w);
                    currentWidth = wWidth;
                }
            }
        }
        if (sb.Length > 0)
        {
            lines.Add(sb.ToString());
        }
        var total = lineHeight * lines.Count;
        return new TextMeasurementResult
        {
            Lines = lines,
            LineHeightPx = lineHeight,
            TotalHeightPx = total
        };
    }

    /// <summary>
    /// Finds the rightmost breakable dash in <paramref name="word"/> such that the part
    /// before it fits on the current line. Returns the dash index, or -1 if no breakable
    /// dash fits. Browsers break within a token at dashes (UAX #14); we replicate that by
    /// keeping the head on the current line and pushing the dash + tail to the next.
    /// </summary>
    private int FindBreakableDash(string word, float currentWidth, float spaceWidth, float limit, float fontSizePx, FontStyle style, float? letterSpacingPx)
    {
        for (var i = word.Length - 1; i > 0; i--)
        {
            if (!TextRunWrapper.IsBreakableDash(word[i]))
                continue;
            var head = word.AsSpan(0, i).ToString();
            var headWidth = GetWordWidth(head, fontSizePx, style);
            if (letterSpacingPx.HasValue && head.Length > 1)
                headWidth += (head.Length - 1) * letterSpacingPx.Value;
            if (currentWidth + spaceWidth + headWidth <= limit)
                return i;
        }
        // A LEADING dash (UAX #14): the browser breaks AFTER it, keeping the dash on
        // this line and pushing the rest to the next. Without this, a token like
        // "—dijo" is atomic and wraps whole, adding a line the browser doesn't have.
        if (word.Length > 1 && TextRunWrapper.IsBreakableDash(word[0]))
        {
            var head = word.AsSpan(0, 1).ToString();
            var headWidth = GetWordWidth(head, fontSizePx, style);
            if (letterSpacingPx.HasValue && head.Length > 1)
                headWidth += (head.Length - 1) * letterSpacingPx.Value;
            if (currentWidth + spaceWidth + headWidth <= limit)
                return 1; // tail starts right after the dash
        }
        return -1;
    }

    public TextMeasurementResult MeasureRuns(IReadOnlyList<TextRun> runs, float maxWidthPx, float? lineHeightLengthPx = null, float? lineHeightMultiplier = null, float? letterSpacingPx = null, float? wordSpacingPx = null, float? firstLineWidthPx = null)
    {
        ArgumentNullException.ThrowIfNull(runs);
        return TextRunWrapper.Wrap(runs, TextWidthProbes.Create(
            WordWidth: GetWordWidth,
            SpaceWidth: (size, style) => GetWordWidth(" ", size, style),
            CharWidth: (ch, size, style) => MeasureShapedTextWidth(ch.ToString(), size, style)),
            maxWidthPx, lineHeightLengthPx, lineHeightMultiplier, letterSpacingPx, wordSpacingPx, firstLineWidthPx);
    }

    private float GetWordWidth(string word, float fontSizePx, FontStyle style)
    {
        if (_wordWidthCache.TryGetValue((word, fontSizePx, style), out var width)) return width;

        var result = MeasureShapedTextWidth(word, fontSizePx, style);
        _wordWidthCache.TryAdd((word, fontSizePx, style), result);
        return result;
    }

    private float MeasureShapedTextWidth(string text, float fontSizePx, FontStyle style)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0f;
        }
        var shaper = _shapers[(int)style];
        if (shaper.HbFont != null)
        {
            try
            {
                // Shape once per unique word at the fixed scale, then rescale per size.
                if (!_wordAdvanceSumCache.TryGetValue((text, style), out var advanceSum))
                {
                    // No lock: per-thread buffer + immutable shared HBFont. A rare
                    // double-shape of the same word from two threads is harmless
                    // (identical result; TryAdd keeps the first).
                    var buffer = _bufferPerThread.Value!; // the factory never returns null
                    buffer.Reset();
                    buffer.AddUtf8(text);
                    buffer.GuessSegmentProperties();
                    shaper.HbFont.Shape(buffer, []);
                    var positions = buffer.GetGlyphPositionSpan();
                    advanceSum = 0f;
                    foreach (var position in positions)
                    {
                        advanceSum += position.XAdvance;
                    }
                    _wordAdvanceSumCache.TryAdd((text, style), advanceSum);
                }
                return advanceSum * (fontSizePx / shaper.Scale);
            }
            catch
            {
                // Fall through to the legacy path below.
            }
        }
        // Legacy path: the SKShaper (and, defensively, SKFont measurement) is not
        // thread-safe, so it shares the same serialization as the HarfBuzz buffer.
        lock (_shapeLock)
        {
            try
            {
                var font = shaper.GetFont(fontSizePx);
                return shaper.Shaper != null ? shaper.Shaper.Shape(text, font).Width : font.MeasureText(text);
            }
            catch
            {
                return shaper.GetFont(fontSizePx).MeasureText(text);
            }
        }
    }

    public void Dispose()
    {
        // Distinct shapers only: deduped styles share one shaper (and typeface).
        foreach (var shaper in _shapers.Distinct())
        {
            shaper.Dispose();
            shaper.Typeface.Dispose();
        }
        // ThreadLocal.Dispose() disposes every per-thread buffer (they implement IDisposable)
        _bufferPerThread.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// All the shaping state for ONE typeface: the HarfBuzz face/font (native
    /// unitsPerEm scale, OpenType functions), the legacy SKShaper fallback and the
    /// per-size SKFont cache. Shared by every style that resolved to the same typeface.
    /// </summary>
    private sealed class StyleShaper : IDisposable
    {
        public SKTypeface Typeface { get; }
        public Face? HbFace { get; }
        public Font? HbFont { get; }
        public SKShaper? Shaper { get; }
        public ConcurrentDictionary<float, SKFont> FontCache { get; } = [];

        /// <summary>
        /// The scale the HarfBuzz font shapes at: the font's native unitsPerEm, where
        /// glyph advances are exact font units (no rounding) — the browser's convention.
        /// </summary>
        public int Scale { get; }

        public StyleShaper(SKTypeface typeface)
        {
            Typeface = typeface;
            // Shape at the font's native scale (unitsPerEm): advances come out as exact
            // font units with no per-glyph rounding, matching how browsers shape. Shaping
            // is font-size independent at a fixed scale, so a word only ever has to be
            // shaped once; the per-size width is a rescale of the cached advance sum.
            Scale = typeface.UnitsPerEm > 0 ? typeface.UnitsPerEm : 1000;
            try
            {
                using var blob = typeface.OpenStream(out var faceIndex).ToHarfBuzzBlob();

                var face = new Face(blob, faceIndex);
                face.Index = faceIndex;
                face.UnitsPerEm = typeface.UnitsPerEm;
                HbFace = face;
                HbFont = new Font(face);
                HbFont.SetScale(Scale, Scale);
                HbFont.SetFunctionsOpenType();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HarfBuzzTextMeasurer] Failed to create HarfBuzz font: {ex.Message}. Falling back to SKShaper/SKFont.");
                HbFont = null;
            }
            try
            {
                Shaper = new SKShaper(typeface);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HarfBuzzTextMeasurer] Failed to create SKShaper: {ex.Message}. Falling back to SKFont.MeasureText().");
                Shaper = null;
            }
        }

        public SKFont GetFont(float fontSizePx) =>
            FontCache.GetOrAdd(
                fontSizePx,
                static (size, typeface) => new SKFont(typeface, size),
                Typeface);

        public void Dispose()
        {
            HbFont?.Dispose();
            HbFace?.Dispose();
            foreach (var font in FontCache.Values)
            {
                font.Dispose();
            }
            Shaper?.Dispose();
            // The typeface is disposed by the measurer (it may be shared by several shapers).
        }
    }
}
