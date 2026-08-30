using BookHeaven.EbookManager.Entities;

namespace BookHeaven.Core.Features.Reader.Models;

public sealed class ReaderState
{
    public Ebook? Ebook { get; set; }
    public int ChapterNumber { get; set; }
    public int PageNumber { get; set; }
    public int TotalPages { get; set; }
    public int TotalPagesPrev { get; set; }
    public int TotalPagesNext { get; set; }
    public IReadOnlyList<Stylesheet> Styles { get; set; } = [];
    public DateTimeOffset EntryTime { get; set; }
    
    public Chapter? CurrentChapter => Ebook?.Content.Chapters.ElementAtOrDefault(ChapterNumber);
    public Chapter? PreviousChapter => Ebook?.Content.Chapters.ElementAtOrDefault(ChapterNumber - 1);
    public Chapter? NextChapter => Ebook?.Content.Chapters.ElementAtOrDefault(ChapterNumber + 1);
    private TocEntry? CurrentTocEntry => Ebook?.Content.GetChapterFromTableOfContents(CurrentChapter?.Identifier);
    public string ChapterTitle => !string.IsNullOrEmpty(CurrentChapter?.Title) 
        ? CurrentChapter!.Title
        : CurrentTocEntry?.Title ?? string.Empty;
    public int TotalChapters => Ebook?.Content.Chapters.Count ?? 0;
    public int TotalWeight => Ebook?.Content.Chapters.Sum(c => c.Weight) ?? 0;
    
    public decimal ProgressAsPercentage => Ebook is not null && CurrentChapter is not null && TotalWeight != 0
        ? (Ebook.Content.GetTotalWeight(ChapterNumber) + CurrentChapter.WeightPerPage(TotalPages) * PageNumber) 
            / (decimal)TotalWeight * 100
        : 0;
}
