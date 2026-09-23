using System.Text;
using System.Text.RegularExpressions;
using BookHeaven.Core.DOM.Text.Abstractions;
using BookHeaven.Core.DOM.Text.Measurers;

namespace BookHeaven.Core.DOM.Text;

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

    public static TextWidthProbes Create(Func<string, float, FontStyle, float> WordWidth, Func<float, FontStyle, float> SpaceWidth, Func<char, float, FontStyle, float> CharWidth) =>
        new() { WordWidth = WordWidth, SpaceWidth = SpaceWidth, CharWidth = CharWidth };
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

    /// <summary>One word plus the font size and style it renders at and the size/style of the run that owns the collapsed space before it (null = no space in the source). <see cref="PrecomputedWidth"/> is set when the token is a glued unit of several original tokens (a word plus punctuation that follows it with no space); it then overrides the per-word width probe.</summary>
    private readonly record struct Token(string Word, float FontSize, FontStyle Style, float? SpaceOwnerSize, FontStyle? SpaceOwnerStyle, float? PrecomputedWidth = null);

    public static TextMeasurementResult Wrap(IReadOnlyList<TextRun> runs, TextWidthProbes probes, float maxWidthPx,
        float? lineHeightLengthPx = null, float? lineHeightMultiplier = null,
        float? letterSpacingPx = null, float? wordSpacingPx = null, float? firstLineWidthPx = null)
    {
        var tokens = BuildTokens(runs);
        var lines = new List<string>();
        var lineHeights = new List<float>();

        var maxRunSize = 0f;
        foreach (var run in runs) maxRunSize = Math.Max(maxRunSize, run.FontSizePx);
        if (maxRunSize <= 0f) maxRunSize = 1f;

        var fallbackLineHeight = BoxHeight(maxRunSize);

        if (tokens.Count == 0)
        {
            return new TextMeasurementResult { Lines = lines, LineHeights = lineHeights, LineHeightPx = fallbackLineHeight, TotalHeightPx = 0f };
        }

        float SpaceWidth(float size, FontStyle style) => probes.SpaceWidth(size, style) + (wordSpacingPx ?? 0f);

        float WordWidth(string word, float size, FontStyle style)
        {
            var width = probes.WordWidth(word, size, style);
            if (letterSpacingPx.HasValue && word.Length > 1) width += (word.Length - 1) * letterSpacingPx.Value;
            return width;
        }

        // A browser does not break between a word and punctuation that immediately follows
        // it with no space (e.g. "word,"), even across run/style boundaries. Glue such
        // tokens into one unbreakable unit so the fit test uses the combined width.
        for (var i = tokens.Count - 1; i > 0; i--)
        {
            if (tokens[i].SpaceOwnerSize.HasValue) continue;
            var prev = tokens[i - 1];
            var glued = tokens[i];
            var unitWidth = (prev.PrecomputedWidth ?? WordWidth(prev.Word, prev.FontSize, prev.Style))
                           + WordWidth(glued.Word, glued.FontSize, glued.Style);
            tokens[i - 1] = prev with { Word = prev.Word + glued.Word, FontSize = Math.Max(prev.FontSize, glued.FontSize), PrecomputedWidth = unitWidth };
            tokens.RemoveAt(i);
        }

        var current = new List<Token>();
        var currentText = new StringBuilder();
        var currentWidth = 0f;

        void FlushLine()
        {
            if (current.Count == 0) return;
            lines.Add(currentText.ToString());
            var h = 0f;
            foreach (var t in current) h = Math.Max(h, BoxHeight(t.FontSize));
            lineHeights.Add(h);
            current.Clear();
            currentText.Clear();
            currentWidth = 0f;
        }

        void SplitLongWord(Token token)
        {
            var frag = new StringBuilder();
            var fragWidth = 0f;
            foreach (var ch in token.Word)
            {
                var cw = probes.CharWidth(ch, token.FontSize, token.Style);
                if (frag.Length > 0 && letterSpacingPx.HasValue) cw += letterSpacingPx.Value;
                if (fragWidth + cw <= maxWidthPx || frag.Length == 0)
                {
                    frag.Append(ch);
                    fragWidth += cw;
                }
                else
                {
                    lines.Add(frag.ToString());
                    lineHeights.Add(BoxHeight(token.FontSize));
                    frag.Clear();
                    frag.Append(ch);
                    fragWidth = probes.CharWidth(ch, token.FontSize, token.Style);
                }
            }
            if (frag.Length > 0)
            {
                lines.Add(frag.ToString());
                lineHeights.Add(BoxHeight(token.FontSize));
            }
        }

        // Rightmost breakable dash whose head fits the current line (UAX #14); -1 if none.
        int FindBreakableDash(string word, float curWidth, float spaceW, float limit, float size, FontStyle style)
        {
            for (var i = word.Length - 1; i > 0; i--)
            {
                if (!IsBreakableDash(word[i]))
                    continue;
                var head = word.AsSpan(0, i).ToString();
                if (curWidth + spaceW + WordWidth(head, size, style) <= limit)
                    return i;
            }
            // A LEADING dash (UAX #14): the browser breaks AFTER it, keeping the dash
            // on this line and pushing the rest to the next. Without this, a token like
            // "—dijo" is atomic and wraps whole, adding a line the browser lacks.
            if (word.Length > 1 && IsBreakableDash(word[0]))
            {
                var head = word.AsSpan(0, 1).ToString();
                if (curWidth + spaceW + WordWidth(head, size, style) <= limit)
                    return 1; // tail starts right after the dash
            }
            return -1;
        }

        foreach (var token in tokens)
        {
            var wWidth = token.PrecomputedWidth ?? WordWidth(token.Word, token.FontSize, token.Style);

            if (current.Count > 0)
            {
                // The first line may be narrower (text-indent) until it is flushed.
                var limit = lines.Count == 0 && firstLineWidthPx.HasValue ? firstLineWidthPx.Value : maxWidthPx;
                var spaceW = token.SpaceOwnerSize.HasValue && token.SpaceOwnerStyle.HasValue ? SpaceWidth(token.SpaceOwnerSize.Value, token.SpaceOwnerStyle.Value) : 0f;
                if (currentWidth + spaceW + wWidth <= limit)
                {
                    if (token.SpaceOwnerSize.HasValue) currentText.Append(' ');
                    currentText.Append(token.Word);
                    currentWidth += spaceW + wWidth;
                    current.Add(token);
                    continue;
                }

                // The token doesn't fit. A browser may break WITHIN it at a dash
                // (UAX #14): keep the head on this line, push the dash + tail to the
                // next. Without this, a word like "oeste—." wraps whole, adding a line
                // the browser doesn't have.
                var dashIndex = FindBreakableDash(token.Word, currentWidth, spaceW, limit, token.FontSize, token.Style);
                if (dashIndex > 0)
                {
                    var head = token.Word.AsSpan(0, dashIndex).ToString();
                    var tail = token.Word.AsSpan(dashIndex).ToString();
                    currentText.Append(' ').Append(head);
                    currentWidth += spaceW + WordWidth(head, token.FontSize, token.Style);
                    current.Add(new Token(head, token.FontSize, token.Style, token.SpaceOwnerSize, token.SpaceOwnerStyle));
                    FlushLine();
                    var tailToken = new Token(tail, token.FontSize, token.Style, null, null);
                    var tailWidth = WordWidth(tail, token.FontSize, token.Style);
                    if (tailWidth <= maxWidthPx)
                    {
                        current.Add(tailToken);
                        currentText.Append(tail);
                        currentWidth = tailWidth;
                    }
                    else
                    {
                        SplitLongWord(tailToken);
                    }
                    continue;
                }

                FlushLine();
            }

            // Token starts a fresh line.
            var firstLimit = lines.Count == 0 && firstLineWidthPx.HasValue ? firstLineWidthPx.Value : maxWidthPx;
            if (wWidth <= firstLimit || wWidth <= maxWidthPx)
            {
                current.Add(token);
                currentText.Append(token.Word);
                currentWidth = wWidth;
            }
            else
            {
                // Word wider than any line: fragment it character by character.
                SplitLongWord(token);
            }
        }

        FlushLine();

        var maxHeight = 0f;
        var total = 0f;
        foreach (var h in lineHeights) { maxHeight = Math.Max(maxHeight, h); total += h; }

        return new TextMeasurementResult
        {
            Lines = lines,
            LineHeights = lineHeights,
            LineHeightPx = maxHeight,
            TotalHeightPx = total
        };

        // Inline box height for a run: a length line-height fixes every box; a unitless
        // (or "normal") one multiplies the run's own font size.
        float BoxHeight(float fontSize) => lineHeightLengthPx ?? (lineHeightMultiplier ?? NormalLineHeightMultiplier) * fontSize;
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
        var tokens = new List<Token>();
        float? pendingSpace = null;
        FontStyle? pendingSpaceStyle = null;
        foreach (var run in runs)
        {
            if (string.IsNullOrEmpty(run.Text) || run.FontSizePx <= 0f) continue;
            var sawWord = false;
            ForEachSegment(run.Text, part =>
            {
                if (part.Length == 0)
                {
                    // Leading/trailing whitespace of this run: remember its size and
                    // style as the owner of a space that carries over to the next word.
                    if (!pendingSpace.HasValue)
                    {
                        pendingSpace = run.FontSizePx;
                        pendingSpaceStyle = run.Style;
                    }
                    return;
                }
                // A word. The space before it is internal (owned by this run) for any
                // word after the first, otherwise it's a boundary space carried over
                // from a previous run (or absent).
                var space = sawWord ? run.FontSizePx : pendingSpace;
                var spaceStyle = sawWord ? run.Style : pendingSpaceStyle;
                tokens.Add(new Token(part, run.FontSizePx, run.Style, space, spaceStyle));
                sawWord = true;
                pendingSpace = null;
                pendingSpaceStyle = null;
            });
        }
        return tokens;
    }

    /// <summary>CSS-collapsible whitespace (also used by <see cref="HarfBuzzTextMeasurer"/>).</summary>
    [GeneratedRegex(@"[ \t\r\n\f\u0085\u2028\u2029]+", RegexOptions.Compiled)]
    public static partial Regex CollapsibleWhitespace();

    /// <summary>
    /// The exact character set of <see cref="CollapsibleWhitespace"/> as a cheap char test,
    /// so <see cref="ForEachSegment"/> can split without the regex engine.
    /// </summary>
    private static bool IsCollapsibleWhitespace(char c) =>
        c is ' ' or '\t' or '\r' or '\n' or '\f' or '\u0085' or '\u2028' or '\u2029';

    /// <summary>
    /// Invokes <paramref name="onSegment"/> for each maximal word and for the empty
    /// segments that a leading/trailing whitespace run produces, in order — the same
    /// sequence as <c>CollapsibleWhitespace().Split(text)</c> but without the regex
    /// engine and without the <see cref="string"/>[] allocation. Only the word
    /// substrings themselves are allocated (callers need them as cache keys / tokens).
    /// </summary>
    public static void ForEachSegment(string text, Action<string> onSegment)
    {
        var n = text.Length;
        var i = 0;
        var seenWord = false;
        while (i < n)
        {
            if (IsCollapsibleWhitespace(text[i]))
            {
                while (i < n && IsCollapsibleWhitespace(text[i])) i++;
                // A whitespace run yields an empty segment only when it leads (before
                // the first word) or trails (after the last word); internal runs are
                // pure separators and emit nothing — matching Regex.Split with '+'.
                if (!seenWord || i >= n) onSegment(string.Empty);
                continue;
            }
            var start = i;
            while (i < n && !IsCollapsibleWhitespace(text[i])) i++;
            onSegment(text.AsSpan(start, i - start).ToString());
            seenWord = true;
        }
    }

    /// <summary>
    /// Dashes that create a break opportunity (UAX #14 BA class): hyphen, figure dash,
    /// en dash, em dash, horizontal bar. The ASCII hyphen-minus (U+002D) is NOT breakable
    /// (LB13/LB21 forbid breaks around it), matching browser behavior.
    /// </summary>
    public static bool IsBreakableDash(char c) =>
        c is '\u2010' // hyphen
        or '\u2012' // figure dash
        or '\u2013' // en dash
        or '\u2014' // em dash
        or '\u2015'; // horizontal bar
}
