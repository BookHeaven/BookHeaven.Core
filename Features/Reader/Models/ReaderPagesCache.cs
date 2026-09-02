namespace BookHeaven.Core.Features.Reader.Models;

public class ReaderPagesCache
{
    public string ReaderSettingsHash { get; set; } = string.Empty;
    public int[] CachedPages { get; set; } = [];
}