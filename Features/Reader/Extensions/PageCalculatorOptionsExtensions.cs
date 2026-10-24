using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using BookHeaven.Core.DOM.Services;
using BookHeaven.Core.Entities;

namespace BookHeaven.Core.Features.Reader.Extensions;

public static class PageCalculatorOptionsExtensions
{
    extension(PageCalculatorOptions options)
    {
        public string CalculateHash()
        {
            var sb = new StringBuilder();
            sb.Append(options.ViewportWidthPx.ToString(CultureInfo.InvariantCulture)).Append('|');
            sb.Append(options.ViewportHeightPx.ToString(CultureInfo.InvariantCulture)).Append('|');
            sb.Append(options.PageWidthPx.ToString(CultureInfo.InvariantCulture)).Append('|');
            sb.Append(options.PageHeightPx.ToString(CultureInfo.InvariantCulture)).Append('|');
            sb.Append(options.LineBreakingMode.ToString()).Append('|');
            sb.Append(options.FontSize.ToString(CultureInfo.InvariantCulture)).Append('|');
            sb.Append(options.LineHeight.ToString(CultureInfo.InvariantCulture)).Append('|');
            sb.Append(options.LetterSpacing.ToString(CultureInfo.InvariantCulture)).Append('|');
            sb.Append(options.WordSpacing.ToString(CultureInfo.InvariantCulture)).Append('|');
            sb.Append(options.ParagraphSpacing.ToString(CultureInfo.InvariantCulture)).Append('|');
            sb.Append(options.TextIndent.ToString(CultureInfo.InvariantCulture)).Append('|');
            sb.Append(options.HorizontalMargin.ToString(CultureInfo.InvariantCulture)).Append('|');
            sb.Append(options.VerticalMargin.ToString(CultureInfo.InvariantCulture)).Append('|');
            sb.Append(options.SelectedFont).Append('|');
            sb.Append(options.ColumnCount);
            var bytes = Encoding.UTF8.GetBytes(sb.ToString());
            var hash = SHA256.HashData(bytes);
            return Convert.ToHexStringLower(hash);
        }
    }
}