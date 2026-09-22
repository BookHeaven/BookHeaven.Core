using System.Diagnostics;
using BookHeaven.Core.Entities;
using BookHeaven.EbookManager.Entities;

namespace BookHeaven.Core.Features.Reader.Models;

public sealed class ReaderState
{
    public Ebook? Ebook { get; private set; }
    public int ChapterNumber { get; private set; }
    public int PageNumber { get; private set; }
    public int PageBookNumber =>  PagesPerChapter.Take(ChapterNumber).Sum() + PageNumber;
    public int TotalPages => PagesPerChapter.ElementAtOrDefault(ChapterNumber);
    public int BookPages => PagesPerChapter.Sum();
    public int[] PagesPerChapter { get; private set; } = [];
    public IReadOnlyList<Stylesheet> Styles => Ebook?.Content.Stylesheets ?? [];
    public Chapter? CurrentChapter => Ebook?.Content.Chapters.ElementAtOrDefault(ChapterNumber);
    private TocEntry? CurrentTocEntry => Ebook?.Content.GetChapterFromTableOfContents(CurrentChapter?.Identifier);
    public string ChapterTitle => !string.IsNullOrEmpty(CurrentTocEntry?.Title)
        ? CurrentTocEntry.Title
        : CurrentChapter?.Title ?? string.Empty; 
    public int TotalChapters => Ebook?.Content.Chapters.Count ?? 0;
    
    public decimal ChapterProgress => TotalPages > 0
        ? (decimal)PageNumber / TotalPages
        : 0;
    public decimal ProgressAsPercentage => BookPages > 0
        ? ((decimal)PageBookNumber / BookPages) * 100
        : 0;
    
    private bool _isSuspended;
    private DateTimeOffset _entryTime;
    private DateTimeOffset _suspendStartTime;
    private TimeSpan _totalSuspendedTime;
    
    public void SetEbook(Ebook ebook)
    {
        Ebook = ebook;
    }
    
    public void StartTimer()
    {
        _entryTime = DateTimeOffset.UtcNow;
    }
    
    public void PauseTimer()
    {
        if(_isSuspended) return;
        _isSuspended = true;
        _suspendStartTime = DateTimeOffset.UtcNow;
    }
    
    public void ResumeTimer()
    {
        if(!_isSuspended) return;
        _isSuspended = false;
        _totalSuspendedTime += DateTimeOffset.UtcNow - _suspendStartTime;
    }

    public TimeSpan GetElapsedReadingTime()
    {
        return DateTimeOffset.UtcNow - _entryTime - _totalSuspendedTime;
    }
    
    public void SetChapterAndPage(int chapterNumber, int pageNumber)
    {
        ChapterNumber = chapterNumber;
        PageNumber = pageNumber;
    }
    
    public void SetPagesPerChapter(int[] pagesPerChapter)
    {
        var lastProgress = ChapterProgress;
        PagesPerChapter = pagesPerChapter;
        SetChapterAndPage(ChapterNumber, 
            lastProgress > 0 
                ? (int)Math.Round(lastProgress * TotalPages) 
                : 1);
    }
}
