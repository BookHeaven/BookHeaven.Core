using System.Collections.Concurrent;
using System.Text;
using BookHeaven.Core.DOM.Text.Abstractions;
using BookHeaven.Core.DOM.Text.Enums;
using BookHeaven.Core.DOM.Text.Helpers;
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
    // Word width per (word, fontSize, style). The outer key is a plain string; the
    // entry fast-paths the single (fontSize, style) most words are seen at and only
    // grows a Dictionary when a second variant appears — no boxing, no per-lookup
    // allocation (the old tuple-keyed caches boxed a ValueTuple on every call).
    private readonly ConcurrentDictionary<string, WordWidthEntry> _wordWidthCache = [];
    // Advance sum per (text, style), shaped once at the font's native scale. One
    // float[] per unique text indexed by style (NaN = not shaped yet) — no boxing.
    private readonly ConcurrentDictionary<string, float[]> _wordAdvanceSumCache = [];
    // One reusable line builder per shaping thread: Measure is called thousands of
    // times per book, and a fresh StringBuilder per call was a steady allocation
    // source. Lines are copied out via ToString before the buffer is reused.
    private readonly ThreadLocal<StringBuilder> _lineBuilder = new(() => new StringBuilder(512));
    // The width probes are stable for the life of the measurer (the delegates capture
    // this), so they're built once here instead of per MeasureRuns call (which boxed
    // three delegates per text block).
    private readonly TextWidthProbes _probes;

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

        _probes = TextWidthProbes.Create(
            wordWidth: GetWordWidth,
            spaceWidth: (size, style) => GetWordWidth(" ", size, style),
            charWidth: (ch, size, style) => MeasureShapedTextWidth(ch.ToString(), size, style));
    }

    /// <summary>
    /// Convenience overload: a font DIRECTORY holding one file per <see cref="FontStyle"/>
    /// variant (or the platform default typeface when <paramref name="fontDirectory"/> is
    /// null, empty or missing). Files are matched to styles by file name: a name
    /// containing "bold" and "italic" is BoldItalic, "bold" is Bold, "italic" is Italic,
    /// anything else is Regular — covering both "Family.ttf" and "Family-Regular.ttf"
    /// naming. Missing variants fall back through the style's fallback chain.
    /// </summary>
    public HarfBuzzTextMeasurer(string? fontDirectory = null)
        : this(ResolveFontPaths(fontDirectory))
    {
    }

    private static IReadOnlyDictionary<FontStyle, string?> ResolveFontPaths(string? fontDirectory)
    {
        var paths = new Dictionary<FontStyle, string?>();
        if (string.IsNullOrWhiteSpace(fontDirectory))
        {
            return paths;
        }
        if (!Directory.Exists(fontDirectory))
        {
            Console.WriteLine($"[HarfBuzzTextMeasurer] Font directory not found: '{fontDirectory}'. Using platform default typeface.");
            return paths;
        }
        foreach (var file in Directory.EnumerateFiles(fontDirectory))
        {
            var name = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
            var style = name.Contains("bold") && name.Contains("italic") ? FontStyle.BoldItalic
                : name.Contains("bold") ? FontStyle.Bold
                : name.Contains("italic") ? FontStyle.Italic
                : FontStyle.Regular;
            paths.TryAdd(style, file);
        }
        return paths;
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
        FontStyle style = FontStyle.Regular,
        bool includeLineText = true)
    {
        if (string.IsNullOrEmpty(text) || fontSizePx <= 0f)
        {
            return new TextMeasurementResult();
        }
        var lineHeight = ITextMeasurer.QuantizeLineHeight(lineHeightPx ?? (fontSizePx * ITextMeasurer.NormalLineHeightMultiplier));
        var lines = new List<string>();
        if (string.IsNullOrEmpty(text))
        {
            return new TextMeasurementResult
            {
                Lines = lines,
                LineCount = 0,
                LineHeightPx = lineHeight,
                TotalHeightPx = 0f
            };
        }
        if (maxWidthPx <= 0f)
        {
            if (includeLineText) lines.Add(text);
            return new TextMeasurementResult
            {
                Lines = lines,
                LineCount = 1,
                LineHeightPx = lineHeight,
                TotalHeightPx = lineHeight
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

        var sb = includeLineText ? _lineBuilder.Value : null;
        sb?.Clear();
        var currentWidth = 0f;
        var lineCount = 0;
        var lineEmpty = true;

        TextRunWrapper.ForEachSegment(text, seg =>
        {
            if (seg.Word.Length == 0)
            {
                // Leading/trailing separator: a browser collapses and drops it.
                return;
            }
            // Fast path: the vast majority of segments contain no embedded
            // object placeholder, so skip the Split array allocation entirely.
            if (seg.Word.IndexOf('\uFFFC') < 0)
            {
                PlaceWord(seg.Word, seg.SpaceBefore);
            }
            else
            {
                var segments = seg.Word.Split('\uFFFC');
                for (var i = 0; i < segments.Length; i++)
                {
                    if (i > 0) ForceBreak();
                    if (segments[i].Length == 0) continue;
                    // The first sub-segment uses the segment's own separator; after a
                    // <br> the space state is reset (no space).
                    PlaceWord(segments[i], i == 0 ? seg.SpaceBefore : false);
                }
            }
        });
        if (!lineEmpty)
        {
            if (sb is not null)
            {
                lines.Add(sb.ToString());
            }
            lineCount++;
        }
        var total = lineHeight * lineCount;
        return new TextMeasurementResult
        {
            Lines = lines,
            LineCount = lineCount,
            LineHeightPx = lineHeight,
            TotalHeightPx = total
        };

        void PlaceWord(string w, bool spaceBefore)
        {
            var limit = lineCount == 0 && firstLineWidthPx.HasValue ? firstLineWidthPx.Value : maxWidthPx;
            var wWidth = GetWordWidth(w, fontSizePx, style);
            if (letterSpacingPx.HasValue && w.Length > 1)
            {
                wWidth += (w.Length - 1) * letterSpacingPx.Value;
            }
            // A real space has width; a zero-width break (U+200B) is a break
            // opportunity with no width, so it adds nothing between the words.
            var spaceW = spaceBefore ? spaceWidth : 0f;
            var tokenWidth = lineEmpty ? wWidth : currentWidth + spaceW + wWidth;
            if (tokenWidth <= limit || lineEmpty)
            {
                if (!lineEmpty && spaceBefore)
                {
                    sb?.Append(' ');
                }
                sb?.Append(w);
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
                var dashIndex = TextRunWrapper.FindBreakableDash(w, currentWidth, spaceW, limit, head =>
                {
                    var width = GetWordWidth(head, fontSizePx, style);
                    if (letterSpacingPx.HasValue && head.Length > 1)
                        width += (head.Length - 1) * letterSpacingPx.Value;
                    return width;
                });
                if (dashIndex > 0)
                {
                    var head = w.AsSpan(0, dashIndex).ToString();
                    var tail = w.AsSpan(dashIndex).ToString();
                    var headWidth = GetWordWidth(head, fontSizePx, style);
                    if (letterSpacingPx.HasValue && head.Length > 1)
                        headWidth += (head.Length - 1) * letterSpacingPx.Value;
                    if (spaceBefore) sb?.Append(' ');
                    sb?.Append(head);
                    currentWidth += spaceW + headWidth;
                    if (sb is not null)
                    {
                        lines.Add(sb.ToString());
                        sb.Clear();
                    }
                    lineCount++;
                    sb?.Append(tail);
                    currentWidth = GetWordWidth(tail, fontSizePx, style);
                    if (letterSpacingPx.HasValue && tail.Length > 1)
                        currentWidth += (tail.Length - 1) * letterSpacingPx.Value;
                }
                else
                {
                    if (sb is not null)
                    {
                        lines.Add(sb.ToString());
                        sb.Clear();
                    }
                    lineCount++;
                    sb?.Append(w);
                    currentWidth = wWidth;
                }
            }

            lineEmpty = false;
        }

        // A <br> (U+FFFC) materializes the current line — even an empty one — and
        // starts a new line; a trailing break at the end of the text adds nothing
        // (the final flush below skips an empty line).
        void ForceBreak()
        {
            if (sb is not null)
            {
                lines.Add(sb.ToString());
                sb.Clear();
            }
            lineCount++;
            currentWidth = 0f;
            lineEmpty = true;
        }
    }

    public TextMeasurementResult MeasureRuns(IReadOnlyList<TextRun> runs, float maxWidthPx, float? lineHeightLengthPx = null, float? lineHeightMultiplier = null, float? letterSpacingPx = null, float? wordSpacingPx = null, float? firstLineWidthPx = null, bool includeLineText = true)
    {
        ArgumentNullException.ThrowIfNull(runs);
        return TextRunWrapper.Wrap(runs, _probes,
            maxWidthPx, lineHeightLengthPx, lineHeightMultiplier, letterSpacingPx, wordSpacingPx, firstLineWidthPx, includeLineText);
    }

    public SKFontMetrics GetFontMetrics(float fontSizePx, FontStyle style)
    {
        return _shapers[(int)style].GetFont(fontSizePx).Metrics;
    }

    public float GetNaturalLineHeight(float fontSizePx, FontStyle style)
    {
        var metrics = _shapers[(int)style].GetFont(fontSizePx).Metrics;
        // Skia reports ascent as negative; the natural line height includes the leading.
        return -metrics.Ascent + metrics.Descent + metrics.Leading;
    }

    private float GetWordWidth(string word, float fontSizePx, FontStyle style)
    {
        if (!_wordWidthCache.TryGetValue(word, out var entry))
        {
            entry = _wordWidthCache.GetOrAdd(word, static _ => new WordWidthEntry());
        }
        var code = PackWidthCode(fontSizePx, style);
        if (entry.Code == code) return entry.Width;
        var extra = entry.Extra;
        if (extra is not null && extra.TryGetValue(code, out var width)) return width;

        var result = MeasureShapedTextWidth(word, fontSizePx, style);
        lock (entry)
        {
            if (entry.Code == code) return entry.Width;
            if (entry.Extra is not null)
            {
                if (entry.Extra.TryGetValue(code, out width)) return width;
                entry.Extra[code] = result;
            }
            else if (entry.Code == 0)
            {
                // Width before Code: Code doubles as the "set" flag, so a concurrent
                // reader that observes the new Code also sees the new Width.
                entry.Width = result;
                entry.Code = code;
            }
            else
            {
                entry.Extra = new Dictionary<long, float> { [entry.Code] = entry.Width, [code] = result };
            }
        }
        return result;
    }

    public float MeasureTextWidth(string text, float fontSizePx, FontStyle style)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0f;
        }
        return GetWordWidth(text, fontSizePx, style);
    }

    // Packs (fontSize, style) into one long: the float's IEEE-754 bits in the high
    // 32 bits, the style index (0-3) in the low 2. Equal floats have equal bits, so
    // the code is a stable identity for the pair and a Dictionary key that boxes
    // nothing.
    private static long PackWidthCode(float fontSizePx, FontStyle style) =>
        ((long)BitConverter.SingleToInt32Bits(fontSizePx) << 2) | (long)(int)style;

    // One slot per style, NaN until shaped. Only invoked on a cache miss (see the
    // TryGetValue-first pattern at the call site), so hits allocate nothing.
    private static float[] CreateAdvanceSums()
    {
        var sums = new float[FallbackChains.Length];
        Array.Fill(sums, float.NaN);
        return sums;
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
                // The per-text float[] is indexed by style (NaN = not shaped yet), so
                // the lookup boxes nothing.
                if (!_wordAdvanceSumCache.TryGetValue(text, out var advanceSums))
                {
                    advanceSums = _wordAdvanceSumCache.GetOrAdd(text, static _ => CreateAdvanceSums());
                }
                var styleIndex = (int)style;
                var advanceSum = advanceSums[styleIndex];
                if (float.IsNaN(advanceSum))
                {
                    // No lock: per-thread buffer + immutable shared HBFont. A rare
                    // double-shape of the same word from two threads is harmless
                    // (identical result; the float write is atomic).
                    var buffer = _bufferPerThread.Value!; // the factory never returns null
                    buffer.Reset();
                    // AddUtf16 takes the string's chars directly (no UTF8 byte[] copy
                    // per unique word, as AddUtf8 would allocate).
                    buffer.AddUtf16(text.AsSpan());
                    buffer.GuessSegmentProperties();
                    shaper.HbFont.Shape(buffer);
                    var positions = buffer.GetGlyphPositionSpan();
                    advanceSum = 0f;
                    foreach (var position in positions)
                    {
                        advanceSum += position.XAdvance;
                    }
                    advanceSums[styleIndex] = advanceSum;
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
    /// Per-word width cache entry. The common case — a word seen at ONE (fontSize,
    /// style) — is two fields read lock-free; a word seen at a second variant grows
    /// a <see cref="Dictionary{TKey,TValue}"/> keyed by the packed code, guarded by
    /// a lock on this entry.
    /// </summary>
    private sealed class WordWidthEntry
    {
        // Packed (fontSize, style) of the first variant; 0 = not set.
        public long Code;
        public float Width;
        public Dictionary<long, float>? Extra;
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

        public SKFont GetFont(float fontSizePx)
        {
            // TryGetValue first: GetOrAdd would invoke the factory (allocating a
            // SKFont) on every call, even a hit, and discard it.
            if (FontCache.TryGetValue(fontSizePx, out var font)) return font;
            return FontCache.GetOrAdd(fontSizePx, static (size, typeface) => new SKFont(typeface, size), Typeface);
        }

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
