using System.Globalization;
using BookHeaven.Core.DOM.CSS.Models;
using BookHeaven.Core.DOM.HTML.Models;
using BookHeaven.Core.DOM.Services;
using SkiaSharp;

namespace BookHeaven.Core.DOM.Engine.Helpers;

/// <summary>
/// Geometry of image (replaced) elements: the CSS replaced-element sizing model
/// (computed width/height, then attributes, then the intrinsic size from the
/// cached file, deriving the missing dimension from the aspect ratio) and the
/// baseline-model line growth an image imposes on the line box that contains it.
/// </summary>
public static class ImageHelpers
{
    /// <summary>True for the tags the engine treats as image (replaced) elements.</summary>
    public static bool IsImageElement(Element el)
    {
        return el.TagName.ToUpperInvariant() switch
        {
            "IMG" or "IMAGE" => true,
            _ => false
        };
    }

    /// <summary>First descendant image element with a non-empty src/href, if any.</summary>
    public static Element? FindImageWithSrc(Element el)
    {
        foreach (var child in el.Children)
        {
            if (IsImageElement(child) && (child.GetAttribute("src") is { Length: > 0 } || child.GetAttribute("href") is { Length: > 0 }))
                return child;
            var nested = FindImageWithSrc(child);
            if (nested != null) return nested;
        }
        return null;
    }

    /// <summary>
    /// Computes the box size of an image element (CSS replaced-element model):
    /// computed width/height, then attributes, then the intrinsic size from the
    /// cached file, deriving the missing dimension from the aspect ratio.
    /// </summary>
    public static (float Width, float Height) MeasureImageSize(
        Element el, CssStyle computed,
        Func<string?, float, float?, float?> parseLength,
        PageCalculatorOptions options, CoreOptions coreOptions,
        float availableWidth, float containerRefWidth, float fontSize,
        CssStyle? srcComputed = null)
    {
        // A wrapper (e.g. <figure>) carries no src of its own: use the first
        // descendant image that does.
        var srcEl = el.GetAttribute("src") is { Length: > 0 } || el.GetAttribute("href") is { Length: > 0 }
            ? el
            : FindImageWithSrc(el) ?? el;

        // The replaced element's box (width/height/max-*) is declared on the
        // element that carries the src, not on a wrapper. When <paramref name="el"/>
        // is a wrapper (e.g. <figure>), the caller passes the descendant image's
        // computed style so its own CSS (e.g. `width: 25%`) is honoured.
        var srcStyle = srcComputed ?? computed;

        var imgWidth = float.NaN;
        var imgHeight = float.NaN;

        bool computedWidthParsed = false, computedHeightParsed = false;
        bool attrWidthParsed = false, attrHeightParsed = false;
        float parsedAttrWidth = 0f, parsedAttrHeight = 0f;
        float computedWidth = 0f, computedHeight = 0f;

        var wp = parseLength(srcStyle.GetPropertyValue("width"), containerRefWidth, fontSize);
        var hp = parseLength(srcStyle.GetPropertyValue("height"), options.PageHeightPx, fontSize);
        if (wp is > 0) { imgWidth = wp.Value; computedWidthParsed = true; computedWidth = wp.Value; }
        if (hp is > 0) { imgHeight = hp.Value; computedHeightParsed = true; computedHeight = hp.Value; }

        if (srcEl.GetAttribute("width") is { } aw && !string.IsNullOrWhiteSpace(aw))
        {
            if (float.TryParse(aw, NumberStyles.Float, CultureInfo.InvariantCulture, out var aval))
            {
                attrWidthParsed = true;
                parsedAttrWidth = aval;
            }
            else
            {
                var px = parseLength(aw, containerRefWidth, fontSize);
                if (px.HasValue) { attrWidthParsed = true; parsedAttrWidth = px.Value; }
            }
        }
        if (srcEl.GetAttribute("height") is { } ah && !string.IsNullOrWhiteSpace(ah))
        {
            if (float.TryParse(ah, NumberStyles.Float, CultureInfo.InvariantCulture, out var aval))
            {
                attrHeightParsed = true;
                parsedAttrHeight = aval;
            }
            else
            {
                var px = parseLength(ah, options.PageHeightPx, fontSize);
                if (px.HasValue) { attrHeightParsed = true; parsedAttrHeight = px.Value; }
            }
        }

        if (float.IsNaN(imgWidth) && attrWidthParsed) imgWidth = parsedAttrWidth;
        if (float.IsNaN(imgHeight) && attrHeightParsed) imgHeight = parsedAttrHeight;

        // Intrinsic image size from cache (a width-only or height-only
        // declaration must keep the image's aspect ratio).
        var intrinsicParsed = false;
        int intrinsicW = 0, intrinsicH = 0;
        if (float.IsNaN(imgWidth) || float.IsNaN(imgHeight))
        {
            try
            {
                var srcAttr = srcEl.GetAttribute("src") ?? srcEl.GetAttribute("href") ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(srcAttr))
                {
                    var clean = srcAttr.Split('#')[0].Split('?')[0].Trim();
                    if (clean.StartsWith("/cache/", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(coreOptions.CachePath))
                    {
                        var relative = clean["/cache/".Length..].TrimStart('/', '\\');
                        var parts = relative.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
                        var pathParts = new List<string> { coreOptions.CachePath };
                        pathParts.AddRange(parts);
                        var imagePath = Path.Combine(pathParts.ToArray());
                        if (File.Exists(imagePath))
                        {
                            using var fileStream = File.OpenRead(imagePath);
                            using var stream = new SKManagedStream(fileStream);
                            using var codec = SKCodec.Create(stream);
                            if (codec != null)
                            {
                                intrinsicW = codec.Info.Width;
                                intrinsicH = codec.Info.Height;
                                intrinsicParsed = intrinsicW > 0 && intrinsicH > 0;
                            }
                        }
                    }
                }
            }
            catch { }
        }

        var mw = parseLength(srcStyle.GetPropertyValue("max-width"), availableWidth, fontSize);
        var maxW = mw is > 0 ? Math.Min(availableWidth, mw.Value) : availableWidth;
        if (imgWidth > maxW) imgWidth = maxW;

        // Derive the missing dimension from the intrinsic aspect ratio
        // (CSS replaced-element model).
        if (intrinsicParsed)
        {
            bool widthMissing = float.IsNaN(imgWidth) || imgWidth <= 0;
            bool heightMissing = float.IsNaN(imgHeight) || imgHeight <= 0;
            if (widthMissing && heightMissing)
            {
                imgWidth = Math.Min(intrinsicW, maxW);
                imgHeight = imgWidth * (intrinsicH / (float)intrinsicW);
            }
            else if (heightMissing)
            {
                imgHeight = imgWidth * (intrinsicH / (float)intrinsicW);
            }
            else if (widthMissing)
            {
                imgWidth = imgHeight * (intrinsicW / (float)intrinsicH);
                if (imgWidth > maxW)
                {
                    imgWidth = maxW;
                    imgHeight = imgWidth * (intrinsicH / (float)intrinsicW);
                }
            }
        }

        if (attrWidthParsed && attrHeightParsed)
        {
            if (parsedAttrWidth > 0) imgHeight = imgWidth * (parsedAttrHeight / parsedAttrWidth);
        }
        else if (computedWidthParsed && computedHeightParsed)
        {
            if (computedWidth > 0)
            {
                if (Math.Abs(imgWidth - computedWidth) > 0.001f) imgHeight = imgWidth * (computedHeight / computedWidth);
                else imgHeight = computedHeight;
            }
        }

        if (float.IsNaN(imgHeight) || imgHeight <= 0)
        {
            if (options.DefaultImageAspectRatio > 0) imgHeight = imgWidth / options.DefaultImageAspectRatio;
            else imgHeight = options.DefaultImageHeightPx;
        }

        var mh = parseLength(srcStyle.GetPropertyValue("max-height"), options.PageHeightPx, fontSize);
        if (mh is > 0 && !float.IsNaN(imgHeight) && imgHeight > mh.Value) imgHeight = mh.Value;

        return (imgWidth, imgHeight);
    }

    /// <summary>
    /// Computes the strut descent the way Chromium does for a line of height
    /// <paramref name="lineHeightPx"/>. Chromium rounds the font's natural
    /// metrics to integers (A = round(ascent), D = round(descent)), then
    /// distributes the line height as strutAscent = floor(L/2 + (A-D)/2) with
    /// the descent taking the remainder. A baseline-aligned image of height H
    /// then grows the line to max(strutAscent, H) + strutDescent.
    ///
    /// The symmetric "split the extra lead evenly" model the engine used before
    /// undershoots Chromium by up to ~1px on tall images, which is enough to
    /// shift a whole page of text. The rule here was derived empirically from
    /// headless-Chromium measurements:
    /// 25+ line-height/font-size data points all match it exactly.
    /// </summary>
    public static float ChromiumStrutDescent(float lineHeightPx, SKFontMetrics metrics)
    {
        var a = MathF.Round(-metrics.Ascent);
        var d = MathF.Round(metrics.Descent);
        var strutAscent = MathF.Floor(lineHeightPx / 2f + (a - d) / 2f);
        return lineHeightPx - strutAscent;
    }

    /// <summary>
    /// The height an image of height <paramref name="imageHeight"/> forces on the line
    /// box that contains it, per its <c>vertical-align</c>, following Chromium's
    /// <c>InlineLayoutStateStack::ApplyBaselineShift</c>: the image's metrics (ascent =
    /// H, descent = 0, its baseline at the image's bottom edge) are shifted relative to
    /// the line's baseline, and the line is the union of the shifted metrics and the
    /// strut (ascent = line height − strut descent).
    /// <list type="bullet">
    /// <item><c>baseline</c> (the default): no shift.</item>
    /// <item><c>sub</c>: shifted down by fontSize/5 + 1.</item>
    /// <item><c>super</c>: shifted up by fontSize/3 + 1.</item>
    /// <item><c>middle</c>: centered on the font's x-height.</item>
    /// <item>length/percentage: shifted up by the value (percentages resolve against
    /// the line height, per CSS 2.1 §15.21); unparseable values fall back to baseline.</item>
    /// <item><c>top</c>/<c>bottom</c>/<c>text-top</c>/<c>text-bottom</c>: the line
    /// contains the image, so it contributes its height alone.</item>
    /// </list>
    /// </summary>
    public static float LineContribution(
        string? verticalAlign, float imageHeight, float lineHeightPx, float strutDescent,
        float fontSize, float xHeight,
        Func<string?, float, float?, float?>? parseLength = null)
    {
        var strutAscent = lineHeightPx - strutDescent;
        var v = verticalAlign?.Trim().ToLowerInvariant();

        // Baseline shift in the block direction (+ = down), Chromium's convention.
        float shift = v switch
        {
            null or "" or "baseline" => 0f,
            "sub" => fontSize / 5f + 1f,
            "super" => -(fontSize / 3f + 1f),
            "middle" or "baseline-middle" => (imageHeight - xHeight) / 2f,
            "top" or "bottom" or "text-top" or "text-bottom" => float.NaN,
            _ => float.TryParse(verticalAlign!, NumberStyles.Float, CultureInfo.InvariantCulture, out var unitless)
                ? -unitless // a positive length moves the box UP
                : (parseLength?.Invoke(verticalAlign, lineHeightPx, fontSize) is { } l ? -l : 0f)
        };
        if (float.IsNaN(shift)) return imageHeight;

        var ascent = imageHeight - shift;
        var descent = shift;
        return MathF.Max(strutAscent, ascent) + MathF.Max(strutDescent, descent);
    }

    /// <summary>
    /// Greedily wraps inline image boxes into lines. Each line gets height
    /// max(lineHeight, tallest image contribution on the line), where the
    /// contribution follows the image's <c>vertical-align</c> (see
    /// <see cref="LineContribution"/>). Adjacent images are separated by an
    /// approximate gap (0.25 × font size); the surveyed epubs have at most one
    /// image per paragraph, so the gap only matters in theory.
    /// </summary>
    public static List<float> BuildImageLines(
        IReadOnlyList<(float Width, float Height, string? VerticalAlign)> images,
        float availableWidth, float lineHeightPx, float strutDescent, float fontSize,
        float xHeight, Func<string?, float, float?, float?>? parseLength = null)
    {
        var lineHeights = new List<float>(images.Count);
        var currentLine = 0f;
        // Images never share a line with text (handled by the caller), so the
        // first image always starts on a fresh line.
        var currentLineWidth = 0f;
        var imageGap = 0.25f * fontSize;

        foreach (var (width, height, verticalAlign) in images)
        {
            if (currentLineWidth > 0 && currentLineWidth + imageGap + width > availableWidth)
            {
                lineHeights.Add(Math.Max(lineHeightPx, currentLine));
                currentLine = 0f;
                currentLineWidth = 0f;
            }
            currentLineWidth += (currentLineWidth > 0 ? imageGap : 0f) + width;
            currentLine = Math.Max(currentLine, LineContribution(verticalAlign, height, lineHeightPx, strutDescent, fontSize, xHeight, parseLength));
        }

        lineHeights.Add(Math.Max(lineHeightPx, currentLine));
        return lineHeights;
    }
}
