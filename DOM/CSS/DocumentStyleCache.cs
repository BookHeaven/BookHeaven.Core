using AngleSharp.Css;
using AngleSharp.Css.Dom;
using AngleSharp.Dom;

namespace BookHeaven.Core.DOM.CSS;

/// <summary>
/// Per-document style computation state, owned by the layout engine that parsed
/// the document. Everything here is released together with the engine (one per
/// chapter), so parallel chapters never share — or leak — style state.
/// </summary>
public sealed class DocumentStyleCache
{
    /// <summary>The document's style collection (UA + author sheets), built once.</summary>
    public IStyleCollection? StyleCollection { get; set; }

    /// <summary>Flattened, device-filtered rule list of <see cref="StyleCollection"/> (the same list the cascade walks).</summary>
    public List<ICssStyleRule>? Rules { get; set; }

    /// <summary>
    /// Per-element rule indices, sorted by (specificity, source order) — the exact
    /// order AngleSharp's cascade applies declarations in.
    /// </summary>
    public Dictionary<IElement, int[]>? RuleMatches { get; set; }

    /// <summary>Raw author declarations index (var()/calc() recovery).</summary>
    public CssParser.RawOverridesIndex? RawIndex { get; set; }

    /// <summary>Resolved content width per element (containing-block memo).</summary>
    public Dictionary<IElement, float> ContentWidth { get; } = [];
}
