using System.Collections.Concurrent;

namespace BookHeaven.Core.DOM;

/// <summary>
/// Per-<c>CalculatePagesAsync</c> cache scope. Every cache the parse/style pipeline
/// memoizes into (global CSS, tag interning, shorthand splits, var() resolution)
/// lives here, so each book calculation starts from a fresh cache and nothing is
/// retained across calls. <c>PageCalculator</c> creates one per call and hands it
/// to every per-chapter engine; the dictionaries are thread-safe because chapters
/// run in parallel workers.
/// </summary>
public sealed class ParseScope
{
    /// <summary>Memoized global stylesheet, keyed by the option values that shape it.</summary>
    public ConcurrentDictionary<string, string> GlobalCss { get; } = [];

    /// <summary>Interned tag names: the book's elements share a handful of string instances.</summary>
    public ConcurrentDictionary<string, string> TagInterns { get; } = [];

    /// <summary>Memoized shorthand splits, keyed by (value, longhand count).</summary>
    public ConcurrentDictionary<(string, int), string[]> ShorthandCache { get; } = [];

    /// <summary>Memoized var() resolution, keyed by (value, custom-property fingerprint).</summary>
    public ConcurrentDictionary<(string, int), string> VarCache { get; } = [];
}
