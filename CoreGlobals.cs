namespace BookHeaven.Core;

public static class CoreGlobals
{
    public static IReadOnlyList<string> SupportedFormats { get; } =
    [
        ".epub",
        ".pdf"
    ];
}