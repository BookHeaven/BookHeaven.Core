namespace BookHeaven.Core;

public static class CoreGlobals
{
    public static string BooksPath { get; internal set; } = string.Empty;
    public static string CoversPath { get; internal set; } = string.Empty;
    public static string FontsPath { get; internal set; } = string.Empty;
    public static string DatabasePath { get; internal set; } = string.Empty;
    public static string CachePath { get; internal set; } = string.Empty;

    public static IReadOnlyList<string> SupportedFormats { get; } =
    [
        ".epub",
        ".pdf"
    ];
}