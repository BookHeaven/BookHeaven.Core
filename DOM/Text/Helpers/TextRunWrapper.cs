using System.Text;
using System.Text.RegularExpressions;
using BookHeaven.Core.DOM.Text.Abstractions;
using BookHeaven.Core.DOM.Text.Enums;
using BookHeaven.Core.DOM.Text.Measurers;

namespace BookHeaven.Core.DOM.Text.Helpers;

/// <summary>
/// Width probes a measurer hands to <see cref="TextRunWrapper"/>: how wide a word, a
/// collapsed space, or a single character render at a given font size and style. Each
/// measurer supplies its own probes (native shaping, Skia, character heuristics); the
/// wrapping algorithm and per-line height math live here exactly once.
/// </summary>
public readonly struct TextWidthProbes
{
    public required Func<string, float, FontStyle, float> WordWidth { get; init; }
    public required Func<float, FontStyle, float> SpaceWidth { get; init; }
    public required Func<char, float, FontStyle, float> CharWidth { get; init; }

    public static TextWidthProbes Create(Func<string, float, FontStyle, float> wordWidth, Func<float, FontStyle, float> spaceWidth, Func<char, float, FontStyle, float> charWidth) =>
        new() { WordWidth = wordWidth, SpaceWidth = spaceWidth, CharWidth = charWidth };
}

/// <summary>
/// Browser-faithful line wrapping for text made of runs at different font sizes
/// (e.g. a drop-cap span inside a heading). Words are measured at their run's own
/// font size; each line's height is the tallest inline box on it (a unitless
/// line-height multiplies each run's size, a length line-height fixes every box).
/// This is the single implementation behind every <see cref="ITextMeasurer.MeasureRuns"/>.
/// </summary>
public static partial class TextRunWrapper
{
    // "normal" line-height approximation, same factor the single-size measurers use.
    private const float NormalLineHeightMultiplier = ITextMeasurer.NormalLineHeightMultiplier;

    // Per-thread scratch buffers: Wrap is called thousands of times per book, and
    // the token/line lists plus the line-text builders were a steady allocation
    // source. Everything is copied out (strings, the LineHeights array) before the
    // buffers are cleared and reused; the returned `lines` list stays per-call.
    private static readonly ThreadLocal<List<Token>> TokenScratch = new(() => new List<Token>(32));
    private static readonly ThreadLocal<List<Token>> CurrentLineScratch = new(() => new List<Token>(16));
    private static readonly ThreadLocal<List<float>> LineHeightsScratch = new(() => new List<float>(8));
    private static readonly ThreadLocal<StringBuilder> LineTextScratch = new(() => new StringBuilder(256));
    private static readonly ThreadLocal<StringBuilder> FragTextScratch = new(() => new StringBuilder(64));

    /// <summary>One word plus the font size and style it renders at and the size/style of the run that owns the collapsed space before it (null = no space in the source). <see cref="PrecomputedWidth"/> is set when the token is a glued unit of several original tokens (a word plus punctuation that follows it with no space); it then overrides the per-word width probe. <see cref="ForcedBreak"/> marks a <c>&lt;br&gt;</c> (U+FFFC): it materializes the current line — even an empty one — and starts a new line.</summary>
    private readonly record struct Token(string Word, float FontSize, FontStyle Style, float? SpaceOwnerSize, FontStyle? SpaceOwnerStyle, float? PrecomputedWidth = null, bool ForcedBreak = false);

    /// <summary>
    /// <paramref name="includeLineText"/>: when false the per-line strings are not
    /// built (no StringBuilder, no line copies) — only the line count and heights are
    /// produced. Line-break decisions depend only on widths, so the wrapping result
    /// (count, heights, total) is identical with or without the text.
    /// </summary>
    public static TextMeasurementResult Wrap(IReadOnlyList<TextRun> runs, TextWidthProbes probes, float maxWidthPx,
        float? lineHeightLengthPx = null, float? lineHeightMultiplier = null,
        float? letterSpacingPx = null, float? wordSpacingPx = null, float? firstLineWidthPx = null, bool includeLineText = true)
    {
        var tokens = BuildTokens(runs);
        var lines = new List<string>();
        var lineHeights = LineHeightsScratch.Value!;
        lineHeights.Clear();

        var maxRunSize = 0f;
        foreach (var run in runs) maxRunSize = Math.Max(maxRunSize, run.FontSizePx);
        if (maxRunSize <= 0f) maxRunSize = 1f;

        var fallbackLineHeight = BoxHeight(maxRunSize);

        if (tokens.Count == 0)
        {
            return new TextMeasurementResult { Lines = lines, LineCount = 0, LineHeightPx = fallbackLineHeight, TotalHeightPx = 0f };
        }

        // A browser does not break between a word and punctuation that immediately follows
        // it with no space (e.g. "word,"), even across run/style boundaries. Glue such
        // tokens into one unbreakable unit so the fit test uses the combined width.
        // Break tokens are never glued: they are line boundaries, not words.
        for (var i = tokens.Count - 1; i > 0; i--)
        {
            if (tokens[i].ForcedBreak || tokens[i - 1].ForcedBreak) continue;
            if (tokens[i].SpaceOwnerSize.HasValue) continue;
            var prev = tokens[i - 1];
            var glued = tokens[i];
            var unitWidth = (prev.PrecomputedWidth ?? WordWidth(prev.Word, prev.FontSize, prev.Style))
                           + WordWidth(glued.Word, glued.FontSize, glued.Style);
            tokens[i - 1] = prev with { Word = prev.Word + glued.Word, FontSize = Math.Max(prev.FontSize, glued.FontSize), PrecomputedWidth = unitWidth };
            tokens.RemoveAt(i);
        }

        var current = CurrentLineScratch.Value!;
        current.Clear();
        var currentText = includeLineText ? LineTextScratch.Value : null;
        currentText?.Clear();
        var currentWidth = 0f;

        foreach (var token in tokens)
        {
            if (token.ForcedBreak)
            {
                // A <br> materializes the current line (even an empty one) and starts
                // a new line; the empty line's height is the break's own box height.
                FlushLine(true, BoxHeight(token.FontSize));
                continue;
            }

            var wWidth = token.PrecomputedWidth ?? WordWidth(token.Word, token.FontSize, token.Style);

            if (current.Count > 0)
            {
                // The first line may be narrower (text-indent) until it is flushed.
                var limit = lineHeights.Count == 0 && firstLineWidthPx.HasValue ? firstLineWidthPx.Value : maxWidthPx;
                var spaceW = token.SpaceOwnerSize.HasValue && token.SpaceOwnerStyle.HasValue ? SpaceWidth(token.SpaceOwnerSize.Value, token.SpaceOwnerStyle.Value) : 0f;
                if (currentWidth + spaceW + wWidth <= limit)
                {
                    if (token.SpaceOwnerSize.HasValue) currentText?.Append(' ');
                    currentText?.Append(token.Word);
                    currentWidth += spaceW + wWidth;
                    current.Add(token);
                    continue;
                }

                // The token doesn't fit. A browser may break WITHIN it at a dash
                // (UAX #14): keep the head on this line, push the dash + tail to the
                // next. Without this, a word like "oeste—." wraps whole, adding a line
                // the browser doesn't have.
                var dashIndex = FindBreakableDash(token.Word, currentWidth, spaceW, limit, head => WordWidth(head, token.FontSize, token.Style));
                if (dashIndex > 0)
                {
                    var head = token.Word.AsSpan(0, dashIndex).ToString();
                    var tail = token.Word.AsSpan(dashIndex).ToString();
                    currentText?.Append(' ').Append(head);
                    currentWidth += spaceW + WordWidth(head, token.FontSize, token.Style);
                    current.Add(new Token(head, token.FontSize, token.Style, token.SpaceOwnerSize, token.SpaceOwnerStyle));
                    FlushLine(false, 0f);
                    var tailToken = new Token(tail, token.FontSize, token.Style, null, null);
                    var tailWidth = WordWidth(tail, token.FontSize, token.Style);
                    if (tailWidth <= maxWidthPx)
                    {
                        current.Add(tailToken);
                        currentText?.Append(tail);
                        currentWidth = tailWidth;
                    }
                    else
                    {
                        SplitLongWord(tailToken);
                    }
                    continue;
                }

                FlushLine(false, 0f);
            }

            // Token starts a fresh line.
            var firstLimit = lineHeights.Count == 0 && firstLineWidthPx.HasValue ? firstLineWidthPx.Value : maxWidthPx;
            if (wWidth <= firstLimit || wWidth <= maxWidthPx)
            {
                current.Add(token);
                currentText?.Append(token.Word);
                currentWidth = wWidth;
            }
            else
            {
                // Word wider than any line: fragment it character by character.
                SplitLongWord(token);
            }
        }

        // A trailing <br> adds nothing: the final flush skips an empty line.
        FlushLine(false, 0f);

        var maxHeight = 0f;
        var total = 0f;
        foreach (var h in lineHeights) { maxHeight = Math.Max(maxHeight, h); total += h; }

        return new TextMeasurementResult
        {
            Lines = lines,
            LineCount = lineHeights.Count,
            LineHeights = lineHeights.Count > 0 ? lineHeights.ToArray() : [],
            LineHeightPx = maxHeight,
            TotalHeightPx = total
        };

        void SplitLongWord(Token token)
        {
            var frag = includeLineText ? FragTextScratch.Value : null;
            frag?.Clear();
            var fragLen = 0;
            var fragWidth = 0f;
            foreach (var ch in token.Word)
            {
                var cw = probes.CharWidth(ch, token.FontSize, token.Style);
                if (fragLen > 0 && letterSpacingPx.HasValue) cw += letterSpacingPx.Value;
                if (fragWidth + cw <= maxWidthPx || fragLen == 0)
                {
                    frag?.Append(ch);
                    fragLen++;
                    fragWidth += cw;
                }
                else
                {
                    if (frag is not null)
                    {
                        lines.Add(frag.ToString());
                        frag.Clear();
                    }
                    lineHeights.Add(BoxHeight(token.FontSize));
                    frag?.Append(ch);
                    fragLen = 1;
                    fragWidth = probes.CharWidth(ch, token.FontSize, token.Style);
                }
            }
            if (fragLen > 0)
            {
                if (frag is not null) lines.Add(frag.ToString());
                lineHeights.Add(BoxHeight(token.FontSize));
            }
        }

        // <paramref name="materializeEmpty"/>: a <c>&lt;br&gt;</c> flushes the current
        // line even when it is empty (an empty line of the break's own box height);
        // the final flush and wrap flushes only materialize non-empty lines.
        void FlushLine(bool materializeEmpty, float emptyLineHeight)
        {
            if (current.Count == 0)
            {
                if (materializeEmpty)
                {
                    if (currentText is not null) lines.Add(string.Empty);
                    lineHeights.Add(emptyLineHeight);
                }
                return;
            }
            if (currentText is not null)
            {
                lines.Add(currentText.ToString());
                currentText.Clear();
            }
            var h = 0f;
            foreach (var t in current) h = Math.Max(h, BoxHeight(t.FontSize));
            lineHeights.Add(h);
            current.Clear();
            currentWidth = 0f;
        }

        float WordWidth(string word, float size, FontStyle style)
        {
            var width = probes.WordWidth(word, size, style);
            if (letterSpacingPx.HasValue && word.Length > 1) width += (word.Length - 1) * letterSpacingPx.Value;
            return width;
        }

        float SpaceWidth(float size, FontStyle style) => probes.SpaceWidth(size, style) + (wordSpacingPx ?? 0f);

        // Inline box height for a run: a length line-height fixes every box; a unitless
        // (or "normal") one multiplies the run's own font size. Quantized to Blink's
        // LayoutUnit grid so the line boxes match the browser's exactly.
        float BoxHeight(float fontSize) => ITextMeasurer.QuantizeLineHeight(lineHeightLengthPx ?? (lineHeightMultiplier ?? NormalLineHeightMultiplier) * fontSize);
    }

    /// <summary>
    /// Flattens runs into word tokens. Collapsible whitespace becomes a break
    /// opportunity owned by the FIRST run that contains it (browser attribution);
    /// whitespace at run boundaries carries over between runs, whitespace inside a
    /// run is owned by that run.
    /// Note: the regex is quantified (+) so <see cref="Regex.Split"/> collapses
    /// internal runs of whitespace and emits no empty string for them — only
    /// leading/trailing whitespace yields empty parts. The space between two
    /// words of the same run is therefore inferred from the word's position
    /// (any word after the first was preceded by whitespace in the source).
    /// </summary>
    private static List<Token> BuildTokens(IReadOnlyList<TextRun> runs)
    {
        var tokens = TokenScratch.Value!;
        tokens.Clear();
        float? pendingSpace = null;
        FontStyle? pendingSpaceStyle = null;
        foreach (var run in runs)
        {
            if (string.IsNullOrEmpty(run.Text) || run.FontSizePx <= 0f) continue;
            var sawWord = false;
            ForEachSegment(run.Text, seg =>
            {
                if (seg.Word.Length == 0)
                {
                    // Leading/trailing separator of this run: only a real space carries
                    // over as the owner of a boundary space; a zero-width break does not.
                    if (seg.SpaceBefore && !pendingSpace.HasValue)
                    {
                        pendingSpace = run.FontSizePx;
                        pendingSpaceStyle = run.Style;
                    }
                    return;
                }
                // A <br> (U+FFFC) inside the segment is a forced break: it emits a
                // break token and resets the space state (no space after a break).
                if (seg.Word.IndexOf('\uFFFC') < 0)
                {
                    // No break in the segment (the common case): no Split array. A real
                    // space is internal (owned by this run) after the first word, or a
                    // boundary space carried over from a previous run; a zero-width break
                    // (or nothing) owns no space.
                    var space = seg.SpaceBefore ? (sawWord ? run.FontSizePx : pendingSpace) : null;
                    var spaceStyle = seg.SpaceBefore ? (sawWord ? run.Style : pendingSpaceStyle) : null;
                    tokens.Add(new Token(seg.Word, run.FontSizePx, run.Style, space, spaceStyle));
                    sawWord = true;
                    pendingSpace = null;
                    pendingSpaceStyle = null;
                    return;
                }
                var segments = seg.Word.Split('\uFFFC');
                for (var i = 0; i < segments.Length; i++)
                {
                    if (i > 0)
                    {
                        tokens.Add(new Token(string.Empty, run.FontSizePx, run.Style, null, null, null, true));
                        sawWord = false;
                        pendingSpace = null;
                        pendingSpaceStyle = null;
                    }
                    var word = segments[i];
                    if (word.Length == 0) continue;
                    // The first sub-segment uses the segment's own separator; sub-segments
                    // after a <br> use the internal/boundary space (the break resets the
                    // state, so the first word after it owns no space).
                    var spaceBefore = i == 0 ? seg.SpaceBefore : true;
                    var space = spaceBefore ? (sawWord ? run.FontSizePx : pendingSpace) : null;
                    var spaceStyle = spaceBefore ? (sawWord ? run.Style : pendingSpaceStyle) : null;
                    tokens.Add(new Token(word, run.FontSizePx, run.Style, space, spaceStyle));
                    sawWord = true;
                    pendingSpace = null;
                    pendingSpaceStyle = null;
                }
            });
        }
        return tokens;
    }

    /// <summary>CSS-collapsible whitespace (also used by <see cref="HarfBuzzTextMeasurer"/>).</summary>
    [GeneratedRegex(@"[ \t\r\n\f\u0085\u2028\u2029]+", RegexOptions.Compiled)]
    public static partial Regex CollapsibleWhitespace();

    /// <summary>
    /// The exact character set of <see cref="CollapsibleWhitespace"/> as a cheap char test,
    /// so <see cref="ForEachSegment"/> can split without the regex engine. Also the
    /// canonical test for trimming: only these characters are dropped at the edges of
    /// a text run; non-collapsible ones (U+00A0, U+2007, U+202F, U+205F, U+3000, …)
    /// survive and occupy a line, as in a browser.
    /// </summary>
    public static bool IsCollapsibleWhitespace(char c) =>
        c is ' ' or '\t' or '\r' or '\n' or '\f' or '\u0085' or '\u2028' or '\u2029';

    /// <summary>
    /// Zero-width characters that create a line-break opportunity (UAX #14 class ZW):
    /// the zero-width space. The joiners (U+200C/U+200D, class CJ) and the BOM
    /// (U+FEFF, class WJ) do NOT break, so they are deliberately excluded.
    /// </summary>
    public static bool IsZeroWidthBreak(char c) => c is '\u200B';

    /// <summary>
    /// Any character that creates a line-break opportunity: collapsible whitespace
    /// (a real, measurable space) or a zero-width break (a break with no width).
    /// </summary>
    public static bool IsBreakOpportunity(char c) => IsCollapsibleWhitespace(c) || IsZeroWidthBreak(c);

    /// <summary>
    /// A word produced by <see cref="ForEachSegment"/> plus whether the break that
    /// precedes it is a real (measurable) space. <c>SpaceBefore</c> is false for the
    /// first word, for a word preceded only by zero-width breaks, and for the empty
    /// segments a leading/trailing separator yields.
    /// </summary>
    public readonly record struct TextSegment(string Word, bool SpaceBefore);

    /// <summary>
    /// Invokes <paramref name="onSegment"/> for each maximal word and for the empty
    /// segments that a leading/trailing separator run produces, in order — the same
    /// sequence as <c>CollapsibleWhitespace().Split(text)</c> but without the regex
    /// engine and without the <see cref="string"/>[] allocation. Only the word
    /// substrings themselves are allocated (callers need them as cache keys / tokens).
    /// A separator run is a break opportunity made of collapsible whitespace and/or
    /// zero-width breaks; <see cref="TextSegment.SpaceBefore"/> is true only when the
    /// run contains at least one collapsible-whitespace character (a real space).
    /// </summary>
    public static void ForEachSegment(string text, Action<TextSegment> onSegment)
    {
        var n = text.Length;
        var i = 0;
        var seenWord = false;
        var pendingSpace = false;
        while (i < n)
        {
            if (IsBreakOpportunity(text[i]))
            {
                var sawRealSpace = false;
                while (i < n && IsBreakOpportunity(text[i]))
                {
                    if (IsCollapsibleWhitespace(text[i])) sawRealSpace = true;
                    i++;
                }
                // A separator run yields an empty segment only when it leads (before
                // the first word) or trails (after the last word); internal runs are
                // pure separators and emit nothing — matching Regex.Split with '+'.
                if (!seenWord || i >= n) onSegment(new TextSegment(string.Empty, sawRealSpace));
                pendingSpace = sawRealSpace;
                continue;
            }
            var start = i;
            while (i < n && !IsBreakOpportunity(text[i])) i++;
            onSegment(new TextSegment(text.AsSpan(start, i - start).ToString(), pendingSpace));
            pendingSpace = false;
            seenWord = true;
        }
    }

    /// <summary>
    /// Finds the rightmost breakable dash in <paramref name="word"/> whose head fits on
    /// the current line. Returns the index where the tail begins, or -1 if none. Hyphens
    /// (HY/HH/BA) break only after the dash, so the dash stays on the head line; em
    /// dashes (B2) break on both sides, so the dash goes to whichever side yields the
    /// longer line — except a break after the dash is skipped when the following
    /// character may not start a line (UAX #14 LB13/LB19/LB21), in which case the dash
    /// goes to the tail. <paramref name="headWidth"/> measures a head substring at
    /// the caller's own font size/style/letter-spacing, so the multi-run <see cref="Wrap"/>
    /// and the single-string measurer fast path share this one rule.
    /// </summary>
    public static int FindBreakableDash(string word, float curWidth, float spaceW, float limit, Func<string, float> headWidth)
    {
        for (var i = word.Length - 1; i > 0; i--)
        {
            var ch = word[i];
            if (IsBreakAfterDash(ch))
            {
                if (Fits(word.AsSpan(0, i + 1).ToString()))
                    return i + 1; // tail starts after the dash
            }
            else if (IsBreakBothSidesDash(ch))
            {
                // A break AFTER the dash is only legal if the following character may
                // start a line. LB13/LB19/LB21 forbid a break before IS/EX/CL/SY/QU/BA/
                // HY/NS, so when the dash is followed by one of those (e.g. "Toh—."),
                // the dash must go to the tail (break before the dash), matching the
                // browser.
                var canBreakAfter = i + 1 < word.Length && !IsNoBreakBefore(word[i + 1]);
                if (canBreakAfter && Fits(word.AsSpan(0, i + 1).ToString()))
                    return i + 1; // dash on the head (longer line)
                if (Fits(word.AsSpan(0, i).ToString()))
                    return i; // dash on the tail
            }
        }
        // A LEADING dash: only em dashes (B2) break after a word-initial position;
        // hyphens (HY/HH) do not (LB20a). The dash stays on this line.
        if (word.Length > 1 && IsBreakBothSidesDash(word[0]) && Fits(word.AsSpan(0, 1).ToString()))
            return 1; // tail starts right after the dash
        return -1;

        bool Fits(string head) => curWidth + spaceW + headWidth(head) <= limit;
    }

    /// <summary>
    /// Dashes that create a break opportunity only AFTER the dash (UAX #14 HY/HH/BA):
    /// the ASCII hyphen-minus, the various hyphens, and the figure/en dashes. Rule LB21
    /// forbids a break before them, so the dash stays on the head line (e.g. "Tan-" /
    /// "Stoker"). The non-breaking hyphen (U+2011, GL) is deliberately excluded.
    /// </summary>
    public static bool IsBreakAfterDash(char c) =>
        c is '\u002D' // hyphen-minus (HY)
        or '\u00AD' // soft hyphen (HH)
        or '\u2010' // hyphen (HH)
        or '\u2012' // figure dash (BA)
        or '\u2013' // en dash (BA)
        or '\u2E17' // double oblique hyphen (HH)
        or '\u2E40' // double hyphen (HH)
        or '\u2E5D'; // oblique hyphen (HH)

    /// <summary>
    /// Dashes that create a break opportunity both before and after (UAX #14 B2): the
    /// em dash. Rule LB17 only forbids the "——" pair, so the dash goes to whichever side
    /// yields the longer line.
    /// </summary>
    public static bool IsBreakBothSidesDash(char c) =>
        c is '\u2014'; // em dash (B2)

    /// <summary>
    /// Characters that may not start a line (UAX #14 LB13/LB19/LB21): sentence and
    /// closing punctuation (IS/EX/CL/SY), quotes (QU), and non-starters (BA/HY/NS).
    /// A break before any of these is forbidden, so a preceding B2 dash must move to
    /// the tail line (e.g. "Toh—." breaks as "Toh" / "—.", not "Toh—" / ".").
    /// </summary>
    public static bool IsNoBreakBefore(char c) => c is
        // IS — infix separators (LB13)
        '\u002C' or '\u002E' or '\u003A' or '\u003B'
        // SY — symbols (LB13)
        or '\u002F'
        // EX — exclamation / interrogation (LB13)
        or '\u0021' or '\u003F'
        // CL — closing punctuation (LB13)
        or '\u0029' or '\u005D' or '\u007D'
        // QU — quotation marks (LB19)
        or '\u0022' or '\u0027'
        // BA — break before/after (LB21)
        or '\u007C' or '\u2012' or '\u2013'
        // HY — hyphen (LB21)
        or '\u002D';
}
