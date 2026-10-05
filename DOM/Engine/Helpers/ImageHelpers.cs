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
        return el.TagName is "img" or "image";
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
    /// Explicitly declared dimensions are used as-is (distortion allowed); the
    /// aspect ratio only fills a dimension that is <c>auto</c>.
    /// </summary>
    /// <param name="containerRefHeight">
    /// Height of the containing block in px, or 0 when it is <c>auto</c> (normal
    /// flow). A percentage height resolves against it; against an auto height it
    /// is indeterminate and falls through to the intrinsic size, per CSS.
    /// </param>
    public static (float Width, float Height) MeasureImageSize(
        Element el, CssStyle computed,
        Func<string?, float, float?, float?> parseLength,
        PageCalculatorOptions options, CoreOptions coreOptions,
        float availableWidth, float containerRefWidth, float containerRefHeight, float fontSize,
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

        bool computedWidthParsed = false, attrWidthParsed = false;

        var wp = parseLength(srcStyle.GetPropertyValue("width"), containerRefWidth, fontSize);
        // Percentage heights resolve against the containing block's height, not
        // the page's: with containerRefHeight == 0 a percentage parses to 0 and
        // the dimension stays auto (indeterminate, per CSS).
        var hp = parseLength(srcStyle.GetPropertyValue("height"), containerRefHeight, fontSize);
        if (wp is > 0) { imgWidth = wp.Value; computedWidthParsed = true; }
        if (hp is > 0) imgHeight = hp.Value;

        // Non-positive attribute values are invalid and ignored.
        if (srcEl.GetAttribute("width") is { } aw && !string.IsNullOrWhiteSpace(aw))
        {
            var aval = float.TryParse(aw, NumberStyles.Float | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out var n) && n > 0
                ? n
                : parseLength(aw, containerRefWidth, fontSize);
            if (aval is > 0)
            {
                attrWidthParsed = true;
                if (float.IsNaN(imgWidth)) imgWidth = aval.Value;
            }
        }
        if (srcEl.GetAttribute("height") is { } ah && !string.IsNullOrWhiteSpace(ah))
        {
            var ahal = float.TryParse(ah, NumberStyles.Float | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out var n) && n > 0
                ? n
                : parseLength(ah, containerRefHeight, fontSize);
            if (ahal is > 0 && float.IsNaN(imgHeight)) imgHeight = ahal.Value;
        }

        // box-sizing:border-box: the width/height properties (and attributes) set
        // the border-box, so the content box is the border-box minus padding and
        // border. The replaced-element sizing model (aspect-ratio derivation, max-*
        // clamps) operates on the content box, so convert the specified dimensions
        // here. The intrinsic size is already a content size and is left untouched.
        var isBorderBox = srcStyle.GetPropertyValue("box-sizing").Trim().Equals("border-box", StringComparison.InvariantCultureIgnoreCase);
        float hInset = 0f, vInset = 0f;
        if (isBorderBox)
        {
            (hInset, vInset) = GetImageBoxInsets(srcStyle, parseLength, containerRefWidth, fontSize);
            if (!float.IsNaN(imgWidth) && imgWidth > 0) imgWidth = Math.Max(0f, imgWidth - hInset);
            if (!float.IsNaN(imgHeight) && imgHeight > 0) imgHeight = Math.Max(0f, imgHeight - vInset);
        }

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
        // max-width is the border-box under border-box, so clamp the content box
        // to (max-width − insets).
        if (isBorderBox) maxW = Math.Max(0f, maxW - hInset);
        if (imgWidth > maxW) imgWidth = maxW;

        // Derive the missing (auto) dimension(s) from the intrinsic aspect
        // ratio (CSS replaced-element model). Explicitly declared dimensions
        // are never re-derived: a max-width clamp on an explicit width keeps
        // the explicit height (distortion allowed, per CSS).
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
                imgWidth = Math.Min(imgHeight * (intrinsicW / (float)intrinsicH), maxW);
            }
        }

        // Last-resort fallbacks so the caller never receives a NaN dimension.
        if (float.IsNaN(imgWidth) || imgWidth <= 0) imgWidth = availableWidth;
        if (float.IsNaN(imgHeight) || imgHeight <= 0)
        {
            if (options.DefaultImageAspectRatio > 0) imgHeight = imgWidth / options.DefaultImageAspectRatio;
            else imgHeight = options.DefaultImageHeightPx;
        }

        var mh = parseLength(srcStyle.GetPropertyValue("max-height"), containerRefHeight, fontSize);
        if (mh is > 0)
        {
            // max-height is the border-box under border-box, so cap the content
            // box at (max-height − insets).
            var maxH = isBorderBox ? Math.Max(0f, mh.Value - vInset) : mh.Value;
            if (imgHeight > maxH)
            {
                imgHeight = maxH;
                // A capped auto height re-derives the width to keep the aspect
                // ratio; an explicit width stays (distortion allowed, per CSS).
                if (!computedWidthParsed && !attrWidthParsed && intrinsicParsed)
                    imgWidth = imgHeight * (intrinsicW / (float)intrinsicH);
            }
        }

        return (imgWidth, imgHeight);
    }

    /// <summary>
    /// The image's horizontal and vertical padding + border totals, in px. Used to
    /// convert between the border-box and content-box dimensions (box-sizing) and to
    /// compute the border-box size an inline image imposes on its line box.
    /// </summary>
    public static (float Horizontal, float Vertical) GetImageBoxInsets(
        CssStyle style, Func<string?, float, float?, float?> parseLength, float reference, float? fontSize)
    {
        var padL = parseLength(style.GetPropertyValue("padding-left"), reference, fontSize) ?? 0f;
        var padR = parseLength(style.GetPropertyValue("padding-right"), reference, fontSize) ?? 0f;
        var padT = parseLength(style.GetPropertyValue("padding-top"), reference, fontSize) ?? 0f;
        var padB = parseLength(style.GetPropertyValue("padding-bottom"), reference, fontSize) ?? 0f;
        var borderL = parseLength(style.GetPropertyValue("border-left-width"), reference, fontSize) ?? 0f;
        var borderR = parseLength(style.GetPropertyValue("border-right-width"), reference, fontSize) ?? 0f;
        var borderTop = parseLength(style.GetPropertyValue("border-top-width"), reference, fontSize) ?? 0f;
        var borderB = parseLength(style.GetPropertyValue("border-bottom-width"), reference, fontSize) ?? 0f;
        return (padL + padR + borderL + borderR, padT + padB + borderTop + borderB);
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
