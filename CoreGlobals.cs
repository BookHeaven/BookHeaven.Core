namespace BookHeaven.Core;

public static class CoreGlobals
{
    public static string BooksPath { get; set; } = string.Empty;
    public static string CoversPath { get; set; } = string.Empty;
    public static string FontsPath { get; set; } = string.Empty;
    public static string DatabasePath { get; set; } = string.Empty;

    public static IReadOnlyList<string> SupportedFormats { get; } =
    [
        ".epub",
        ".pdf"
    ];
}