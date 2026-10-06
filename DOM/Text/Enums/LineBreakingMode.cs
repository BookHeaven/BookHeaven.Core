namespace BookHeaven.Core.DOM.Text.Enums;

/// <summary>
///     The line-breaking algorithm a browser engine uses to decide whether a word stays on the
///     current line. The two major engines disagree on the fit test, so the engine must be told
///     which one to emulate to keep pagination in parity with the browser that renders the page.
/// </summary>
public enum LineBreakingMode
{
    Firefox,
    Chrome
}