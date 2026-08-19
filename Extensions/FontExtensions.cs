using BookHeaven.Core.Entities;

namespace BookHeaven.Core.Extensions;

public static class FontExtensions
{
    public static string FilePath(this Font font) => Path.Combine(CoreGlobals.FontsPath, font.Family, font.FileName);
    public static string Url(this Font font) => $"/fonts/{font.Family}/{font.FileName}";

    public static string GetFontFace(this Font font)
    {
        return $@"@font-face {{
            font-family: '{font.Family}';
            src: url('{font.Url()}') format('{font.GetFormat()}');
            {(font.Weight != "all" ? $"font-weight: {font.Weight};" : string.Empty)}
            {(font.Style != "all" ? $"font-style: {font.Style};" : string.Empty)}
        }}";
    }

    private static string GetFormat(this Font font)
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