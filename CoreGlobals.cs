namespace BookHeaven.Core;

public static class CoreGlobals
{
    internal static string BooksPath { get; set; } = null!;
    
    public static IReadOnlyList<string> SupportedFormats { get; } =
    [
        ".epub",
        ".pdf"
    ];
}