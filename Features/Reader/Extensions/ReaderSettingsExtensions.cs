using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using BookHeaven.Core.Entities;

namespace BookHeaven.Core.Features.Reader.Extensions;

public static class ReaderSettingsExtensions
{
    extension(ProfileSettings settings)
    {
        public string CalculateHash()
        {
            var sb = new StringBuilder();
            // Append properties in a stable, explicit order. Exclude IDs and navigation property.
            sb.Append(settings.FontSize.ToString(CultureInfo.InvariantCulture)).Append('|');
            sb.Append(settings.LineHeight.ToString(CultureInfo.InvariantCulture)).Append('|');
            sb.Append(settings.LetterSpacing.ToString(CultureInfo.InvariantCulture)).Append('|');
            sb.Append(settings.WordSpacing.ToString(CultureInfo.InvariantCulture)).Append('|');
            sb.Append(settings.ParagraphSpacing.ToString(CultureInfo.InvariantCulture)).Append('|');
            sb.Append(settings.TextIndent.ToString(CultureInfo.InvariantCulture)).Append('|');
            sb.Append(settings.HorizontalMargin.ToString(CultureInfo.InvariantCulture)).Append('|');
            sb.Append(settings.VerticalMargin.ToString(CultureInfo.InvariantCulture)).Append('|');
            sb.Append(settings.SelectedLayout.ToString(CultureInfo.InvariantCulture)).Append('|');
            sb.Append(settings.SelectedFont);
            var bytes = Encoding.UTF8.GetBytes(sb.ToString());
            var hash = SHA256.HashData(bytes);
            return Convert.ToHexStringLower(hash);
        }
    }
}