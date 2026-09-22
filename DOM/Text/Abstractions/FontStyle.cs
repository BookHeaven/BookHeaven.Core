namespace BookHeaven.Core.DOM.Text.Abstractions;

/// <summary>
/// The four font style variants a family can ship. A text run is measured with the
/// variant that matches its CSS <c>font-weight</c> (normal/bold) and <c>font-style</c>
/// (normal/italic), exactly like a browser picks a face from a family.
/// </summary>
public enum FontStyle
{
    Regular,
    Italic,
    Bold,
    BoldItalic
}
