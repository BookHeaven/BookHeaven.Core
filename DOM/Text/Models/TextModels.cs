using BookHeaven.Core.DOM.Text.Enums;

namespace BookHeaven.Core.DOM.Text.Models;

/// <summary>
/// A stretch of inline text rendered at a single font size and style. A block's inline
/// content is split into runs whenever a child inline element (e.g. a drop-cap
/// <c>&lt;span&gt;</c>) declares its own font size, weight or style, exactly like a
/// browser does.
/// </summary>
public sealed record TextRun(string Text, float FontSizePx, FontStyle Style = FontStyle.Regular);

public sealed class TextMeasurementResult
{
    public IReadOnlyList<string> Lines { get; init; } = [];

    /// <summary>
    /// Number of wrapped lines. Always valid, even when the measurer was asked to
    /// skip building the line text (<c>includeLineText: false</c>, in which case
    /// <see cref="Lines"/> is empty).
    /// </summary>
    public int LineCount { get; init; }

    /// <summary>Height of the tallest line (max of <see cref="LineHeights"/> when present).</summary>
    public float LineHeightPx { get; init; }

    public float TotalHeightPx { get; init; }

    /// <summary>
    /// Per-line heights (one entry per line of <see cref="Lines"/>). Empty when every
    /// line shares the same height (<see cref="LineHeightPx"/>).
    /// </summary>
    public float[] LineHeights { get; init; } = [];
}