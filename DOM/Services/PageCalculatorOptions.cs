namespace BookHeaven.Core.DOM.Services;

public sealed class PageCalculatorOptions
{
    public const int DefaultPageWidthPx = 1024;
    public const int DefaultPageHeightPx = 768;
    public const float DefaultRootFontSizePx = 16;

    /// <summary>Page width in pixels.</summary>
    public int PageWidthPx { get; set; }

    /// <summary>Page height in pixels.</summary>
    public int PageHeightPx { get; set; }

    /// <summary>Root font size in pixels (used to resolve em/rem).</summary>
    public float RootFontSizePx { get; } = DefaultRootFontSizePx;

    /// <summary>Enable hyphenation during line breaking.</summary>
    public bool EnableHyphenation { get; set; } = false;

    /// <summary>Default image aspect ratio expressed as width/height used when intrinsic size is not available. If zero or negative, DefaultImageHeightPx is used instead.</summary>
    public float DefaultImageAspectRatio { get; set; } = 4f / 3f;

    /// <summary>Fallback image height in pixels when aspect ratio is not specified.</summary>
    public float DefaultImageHeightPx { get; set; } = 150f;

    // Profile-like reader settings (same as BookHeaven.Core.Entities.ProfileSettings, excluding Ids and SelectedLayout)
    public float FontSize { get; set; } = DefaultRootFontSizePx;
    public float HorizontalMargin { get; set; }
    public float VerticalMargin { get; set; }
    public float LineHeight { get; set; }
    public float LetterSpacing { get; set; }
    public float WordSpacing { get; set; }
    public float ParagraphSpacing { get; set; }
    public float TextIndent { get; set; }
    public string SelectedFont { get; set; } = string.Empty;

    /// <summary>
    /// Returns a copy where non-positive page dimensions or root font size are
    /// replaced with defaults. Zero values would make length resolution degenerate
    /// (em against a zero font size, % against a zero page width) and the text
    /// measurer rejects non-positive font sizes.
    /// </summary>
    public PageCalculatorOptions Normalize() => new()
    {
        PageWidthPx = PageWidthPx > 0 ? PageWidthPx : DefaultPageWidthPx,
        PageHeightPx = PageHeightPx > 0 ? PageHeightPx : DefaultPageHeightPx,
        EnableHyphenation = EnableHyphenation,
        DefaultImageAspectRatio = DefaultImageAspectRatio,
        DefaultImageHeightPx = DefaultImageHeightPx,
        FontSize = FontSize > 0f ? FontSize : DefaultRootFontSizePx,
        HorizontalMargin = HorizontalMargin,
        VerticalMargin = VerticalMargin,
        LineHeight = LineHeight,
        LetterSpacing = LetterSpacing,
        WordSpacing = WordSpacing,
        ParagraphSpacing = ParagraphSpacing,
        TextIndent = TextIndent,
        SelectedFont = SelectedFont
    };
}
