using BookHeaven.Core.Entities;

namespace BookHeaven.Core.Extensions;

public static class FontExtensions
{
    extension(Font font)
    {
        public string FilePath() => Path.Combine(CoreGlobals.FontsPath, font.Family, font.FileName);
        public string Url() => $"/fonts/{font.Family}/{font.FileName}";

        public string GetFontFace()
        {
            return $@"@font-face {{
            font-family: '{font.Family}';
            src: url('{font.Url()}') format('{font.GetFormat()}');
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