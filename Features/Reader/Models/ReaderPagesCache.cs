namespace BookHeaven.Core.Features.Reader.Models;

public class ReaderPagesCache
{
    public string ReaderSettingsHash { get; set; } = string.Empty;
    public List<CachedChapterPages> CachedPages { get; set; } = [];
}

/// <summary>
/// Page count of a single chapter plus whether it is still pending a recount
/// (e.g. because reader settings changed after it was counted).
/// </summary>
public class CachedChapterPages
{
    public int Pages { get; set; }
    public bool PendingRecount { get; set; }
}