using BookHeaven.Core.Entities;
using BookHeaven.EbookManager.Entities;

namespace BookHeaven.Core.Features.Reader.Models;

public sealed class ReaderState
{
    public Ebook? Ebook { get; private set; }
    public int ChapterNumber { get; private set; }
    public int PageNumber { get; private set; }
    public int PageBookNumber { get; private set; }
    public int TotalPages { get; private set; }
    private int BookPages { get; set; }
    public int[] PagesPerChapter { get; private set; } = [];
    public IReadOnlyList<Stylesheet> Styles { get; private set; } = [];
    public DateTimeOffset EntryTime { get; set; }
    public DateTimeOffset SuspendStartTime { get; set; }
    public TimeSpan TotalSuspendedTime { get; set; }
    
    public Chapter? CurrentChapter { get; private set; }
    private TocEntry? CurrentTocEntry { get; set; }
    public string ChapterTitle { get; private set; } = string.Empty;
    public int TotalChapters { get; private set; }
    
    public decimal ProgressAsPercentage => Ebook is not null && BookPages > 0
        ? ((decimal)PageBookNumber / BookPages) * 100
        : 0;
    
    public void SetEbook(Ebook ebook)
    {
        Ebook = ebook;
        Styles = Ebook.Content.Stylesheets;
        TotalChapters = Ebook!.Content.Chapters.Count;
    }
    
    public void SetChapterAndPage(int chapterNumber, int pageNumber)
    {
        ChapterNumber = chapterNumber;
        TotalPages = PagesPerChapter.ElementAtOrDefault(chapterNumber);
        CurrentChapter = Ebook?.Content.Chapters.ElementAtOrDefault(ChapterNumber);
        CurrentTocEntry = Ebook?.Content.GetChapterFromTableOfContents(CurrentChapter?.Identifier);
        ChapterTitle = !string.IsNullOrEmpty(CurrentTocEntry?.Title)
            ? CurrentTocEntry.Title
            : CurrentChapter?.Title ?? string.Empty;
        
        PageNumber = pageNumber;
        PageBookNumber = PagesPerChapter.Take(ChapterNumber).Sum() + PageNumber;
    }
    
    public void SetPagesPerChapter(int[] pagesPerChapter)
    {
        PagesPerChapter = pagesPerChapter;
        BookPages = pagesPerChapter.Sum();
        SetChapterAndPage(ChapterNumber, PageNumber);
    }
}
