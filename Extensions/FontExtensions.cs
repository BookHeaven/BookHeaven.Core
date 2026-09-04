using BookHeaven.Core.Entities;

namespace BookHeaven.Core.Extensions;

public static class FontExtensions
{
    extension(Font font)
    {
        public string GetFontFace(string url)
        {
            return $@"@font-face {{
            font-family: '{font.Family}';
            src: url('{url}') format('{font.GetFormat()}');
            {(font.Weight != "all" ? $"font-weight: {font.Weight};" : string.Empty)}
            {(font.Style != "all" ? $"font-style: {font.Style};" : string.Empty)}
        }}";
        }

        private string GetFormat()
        {
            return font.FileName.Split(".").Last() switch
            {
                "woff" => "woff",
                "woff2" => "woff2",
                "ttf" => "truetype",
                "otf" => "opentype",
                _ => string.Empty
            };
        }
    }
}