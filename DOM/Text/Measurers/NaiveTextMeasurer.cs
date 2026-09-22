using BookHeaven.Core.DOM.Text.Abstractions;

namespace BookHeaven.Core.DOM.Text.Measurers;

/// <summary>
/// Naive text measurer used as an initial implementation until a shaping-based measurer is added.
/// It uses a simple average-character width heuristic and basic word wrapping. This is sufficient for
/// unit tests and to drive early layout development without native font dependencies.
/// </summary>
public sealed class NaiveTextMeasurer : ITextMeasurer
{
    // average glyph width factor relative to font size (empirical default)
    private const float AvgCharWidthFactor = 0.5f;

    public TextMeasurementResult Measure(string text, float fontSizePx, float maxWidthPx, float? lineHeightPx = null, float? letterSpacingPx = null, float? wordSpacingPx = null, float? firstLineWidthPx = null, FontStyle style = FontStyle.Regular)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));
        if (fontSizePx <= 0) throw new ArgumentOutOfRangeException(nameof(fontSizePx));

        float avgCharWidth = fontSizePx * AvgCharWidthFactor;
        float lineHeight = lineHeightPx ?? (fontSizePx * ITextMeasurer.NormalLineHeightMultiplier);

        var lines = new List<string>();
        if (string.IsNullOrEmpty(text))
            return new TextMeasurementResult { Lines = lines, LineHeightPx = lineHeight, TotalHeightPx = 0 };

        if (maxWidthPx <= 0)
        {
            lines.Add(text);
            return new TextMeasurementResult { Lines = lines, LineHeightPx = lineHeight, TotalHeightPx = lineHeight * lines.Count };
        }

        var words = text.Split(' ');
        string current = string.Empty;
        foreach (var w in words)
        {
            // The first line may have a different (indented) width limit.
            float limit = lines.Count == 0 && firstLineWidthPx.HasValue ? firstLineWidthPx.Value : maxWidthPx;
            var candidate = string.IsNullOrEmpty(current) ? w : current + " " + w;
            // Base width using average char width
            float candidateWidth = candidate.Length * avgCharWidth;
            // add letter-spacing between characters (gaps = length-1)
            int charGaps = Math.Max(0, candidate.Length - 1);
            if (letterSpacingPx.HasValue && charGaps > 0) candidateWidth += charGaps * letterSpacingPx.Value;
            // add extra word-spacing for spaces (number of spaces = wordsCount-1)
            int spaceCount = Math.Max(0, candidate.Split(' ').Length - 1);
            if (wordSpacingPx.HasValue && spaceCount > 0) candidateWidth += spaceCount * wordSpacingPx.Value;

            if (candidateWidth <= limit || string.IsNullOrEmpty(current))
            {
                current = candidate;
            }
            else
            {
                lines.Add(current);
                current = w;
            }
        }
        if (!string.IsNullOrEmpty(current)) lines.Add(current);

        float total = lineHeight * lines.Count;
        return new TextMeasurementResult { Lines = lines, LineHeightPx = lineHeight, TotalHeightPx = total };
    }

    public TextMeasurementResult MeasureRuns(IReadOnlyList<TextRun> runs, float maxWidthPx, float? lineHeightLengthPx = null, float? lineHeightMultiplier = null, float? letterSpacingPx = null, float? wordSpacingPx = null, float? firstLineWidthPx = null)
    {
        if (runs is null) throw new ArgumentNullException(nameof(runs));
        // The heuristic is style-agnostic: the style parameter is ignored.
        return TextRunWrapper.Wrap(runs, TextWidthProbes.Create(
            WordWidth: (word, size, style) => word.Length * size * AvgCharWidthFactor,
            SpaceWidth: (size, style) => size * AvgCharWidthFactor,
            CharWidth: (ch, size, style) => size * AvgCharWidthFactor),
            maxWidthPx, lineHeightLengthPx, lineHeightMultiplier, letterSpacingPx, wordSpacingPx, firstLineWidthPx);
    }
}
