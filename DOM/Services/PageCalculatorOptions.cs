namespace BookHeaven.Core.DOM.Services;

public sealed class PageCalculatorOptions
{
    /// <summary>Root font size in pixels (used to resolve em/rem).</summary>
    public const float RootFontSizePx = 16;

    public int ViewportWidthPx { get; set; } = 1024;
    public int ViewportHeightPx { get; set; } = 768;

    /// <summary>Page width in pixels.</summary>
    public int PageWidthPx { get; set; }

    /// <summary>Page height in pixels.</summary>
    public int PageHeightPx { get; set; }

    /// <summary>Enable hyphenation during line breaking.</summary>
    public bool EnableHyphenation { get; set; } = false;

    /// <summary>Default image aspect ratio expressed as width/height used when intrinsic size is not available. If zero or negative, DefaultImageHeightPx is used instead.</summary>
    public float DefaultImageAspectRatio { get; set; } = 4f / 3f;

    /// <summary>Fallback image height in pixels when aspect ratio is not specified.</summary>
    public float DefaultImageHeightPx { get; set; } = 150f;

    public float FontSize { get; set; } = RootFontSizePx;
    public float HorizontalMargin { get; set; }
    public float VerticalMargin { get; set; }
    public float LineHeight { get; set; }
    public float LetterSpacing { get; set; }
    public float WordSpacing { get; set; }
    public float ParagraphSpacing { get; set; }
    public float TextIndent { get; set; }
    public string SelectedFont { get; set; } = string.Empty;
}
