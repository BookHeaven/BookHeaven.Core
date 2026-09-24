using BookHeaven.Core.Abstractions.Services;
using BookHeaven.Core.DOM.Services;
using BookHeaven.Core.DOM.Text.Abstractions;
using BookHeaven.Core.Entities;
using BookHeaven.Core.Features.Fonts;
using Mediator;

namespace BookHeaven.Core.DOM.Text.Measurers;

/// <summary>
/// Creates the <see cref="ITextMeasurer"/> implementations used by the layout
/// engines. Kept separate from any specific engine so both the Mini pipeline and
/// the page calculator can share ONE measurer (and its warm word-shaping cache)
/// across the parallel layout passes of a single book.
/// </summary>
public static class TextMeasurerFactory
{
    /// <summary>
    /// Creates the text measurer from a single font directory (one file per style
    /// variant). Exposed so callers running several layout passes — e.g. chapters of
    /// one book in parallel — can share ONE measurer and keep its word-shaping cache
    /// warm. Use <see cref="CreateMeasurerAsync"/> to resolve the per-style variants
    /// of <see cref="PageCalculatorOptions.SelectedFont"/> from the database.
    /// </summary>
    public static ITextMeasurer CreateMeasurer(string? defaultFontDirectory = null)
    {
        return new HarfBuzzTextMeasurer(defaultFontDirectory);
    }

    /// <summary>
    /// Creates the text measurer resolving the per-style font variants (Regular, Italic,
    /// Bold, BoldItalic) of <see cref="PageCalculatorOptions.SelectedFont"/> from the
    /// database: <c>GetAllFonts</c> filtered by family, each font's full path built with
    /// <see cref="IUrlBuilder.FontFilePath"/>. A font whose style/weight is "all" (single
    /// file upload) covers every variant it intersects; the most specific font wins each
    /// variant slot. When <see cref="PageCalculatorOptions.SelectedFont"/> is empty, the
    /// default font directory (<see cref="IUrlBuilder.DefaultFontDirectory"/>) is used.
    /// </summary>
    public static async Task<ITextMeasurer> CreateMeasurerAsync(PageCalculatorOptions normalizedOptions, ISender sender, IUrlBuilder urlBuilder, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(normalizedOptions);
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentNullException.ThrowIfNull(urlBuilder);

        if (string.IsNullOrWhiteSpace(normalizedOptions.SelectedFont))
        {
            return CreateMeasurer(urlBuilder.DefaultFontDirectory());
        }

        var result = await sender.Send(new GetAllFonts.Query(normalizedOptions.SelectedFont), cancellationToken);
        if (result.IsSuccess)
        {
            var fontPaths = MapFontsToStyles(result.Value, urlBuilder);
            if (fontPaths.Count > 0)
            {
                return new HarfBuzzTextMeasurer(fontPaths);
            }
        }

        // Selected family not found (or query failed): fall back to the default font
        // directory (platform default typeface when it is empty or missing).
        return CreateMeasurer(urlBuilder.DefaultFontDirectory());
    }

    /// <summary>
    /// Maps the <see cref="Font"/> rows of one family to the four <see cref="FontStyle"/>
    /// slots. A font covers the intersection of its style set (normal → Regular+Bold,
    /// italic → Italic+BoldItalic, all → everything) and its weight set (normal →
    /// Regular+Italic, bold → Bold+BoldItalic, all → everything); when several fonts
    /// cover the same slot, the most specific one (fewest "all" wildcards) wins.
    /// </summary>
    internal static Dictionary<FontStyle, string?> MapFontsToStyles(IReadOnlyList<Font> fonts, IUrlBuilder urlBuilder)
    {
        ArgumentNullException.ThrowIfNull(urlBuilder);

        var assignments = new List<(FontStyle Style, int Specificity, string Path)>();
        foreach (var font in fonts)
        {
            var style = font.Style.Trim().ToLowerInvariant();
            var weight = font.Weight.Trim().ToLowerInvariant();
            var styleSet = style switch
            {
                "italic" => new[] { FontStyle.Italic, FontStyle.BoldItalic },
                "all" => new[] { FontStyle.Regular, FontStyle.Italic, FontStyle.Bold, FontStyle.BoldItalic },
                _ => new[] { FontStyle.Regular, FontStyle.Bold } // "normal"
            };
            var weightSet = weight switch
            {
                "bold" => new[] { FontStyle.Bold, FontStyle.BoldItalic },
                "all" => new[] { FontStyle.Regular, FontStyle.Italic, FontStyle.Bold, FontStyle.BoldItalic },
                _ => new[] { FontStyle.Regular, FontStyle.Italic } // "normal"
            };
            var specificity = (style != "all" ? 2 : 0) + (weight != "all" ? 1 : 0);
            var path = urlBuilder.FontFilePath(font.Family, font.FileName);
            foreach (var slot in styleSet.Intersect(weightSet))
            {
                assignments.Add((slot, specificity, path));
            }
        }

        // Most specific first; the first assignment wins each slot.
        assignments.Sort((a, b) => b.Specificity.CompareTo(a.Specificity));
        var result = new Dictionary<FontStyle, string?>();
        foreach (var (slot, _, path) in assignments)
        {
            result.TryAdd(slot, path);
        }
        return result;
    }
}
