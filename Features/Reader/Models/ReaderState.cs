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
    private bool[] _pendingRecount = [];
    public IReadOnlyList<Stylesheet> Styles => Ebook?.Content.Stylesheets ?? [];
    public DateTimeOffset EntryTime { get; set; }
    public DateTimeOffset SuspendStartTime { get; set; }
    public TimeSpan TotalSuspendedTime { get; set; }
    
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
    
    public void SetEbook(Ebook ebook)
    {
        Ebook = ebook;
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
        _pendingRecount = new bool[pagesPerChapter.Length];
        SetChapterAndPage(ChapterNumber, 
            lastProgress > 0 
                ? (int)Math.Round(lastProgress * TotalPages) 
                : 1);
    }
    
    public void SetCachedPages(CachedChapterPages[] cachedPages)
    {
        var lastProgress = ChapterProgress;
        if (cachedPages.Length == 0)
        {
            PagesPerChapter = new int[TotalChapters];
            _pendingRecount = [.. Enumerable.Repeat(true, TotalChapters)];
        }
        else
        {
            PagesPerChapter = [.. cachedPages.Select(p => p.Pages)];
            _pendingRecount = [.. cachedPages.Select(p => p.PendingRecount)];
        }
        SetChapterAndPage(ChapterNumber, lastProgress > 0 ? (int)Math.Round(lastProgress * TotalPages) : 1);
    }
    
    public void InvalidatePages()
    {
        for (var i = 0; i < _pendingRecount.Length; i++)
        {
            _pendingRecount[i] = true;
        }
    }
    
    public bool SetChapterPageCount(int chapter, int pageCount)
    {
        if (chapter < 0 || chapter >= PagesPerChapter.Length) return false;
        var oldCount = PagesPerChapter[chapter];
        PagesPerChapter[chapter] = pageCount;
        _pendingRecount[chapter] = false;
        
        var pageNumber = PageNumber;
        if (chapter == ChapterNumber)
        {
            if (oldCount > 0 && PageNumber > pageCount)
            {
                // Mantener el mismo progreso dentro del capítulo si el recuento cambia
                pageNumber = (int)Math.Round((double)PageNumber / oldCount * pageCount);
            }
            pageNumber = Math.Clamp(pageNumber, 1, Math.Max(pageCount, 1));
        }
        SetChapterAndPage(ChapterNumber, pageNumber);
        return true;
    }
    
    public bool IsChapterPending(int chapter) =>
        chapter >= 0 && chapter < _pendingRecount.Length && _pendingRecount[chapter];
    
    public bool AnyChaptersPending => _pendingRecount.Any(p => p);
    
    public int[] GetPendingChapterIndices() =>
        _pendingRecount.Select((pending, index) => (pending, index))
            .Where(x => x.pending)
            .Select(x => x.index)
            .ToArray();
    
    public CachedChapterPages[] GetCachedPages() =>
        PagesPerChapter.Select((pages, index) => new CachedChapterPages
        {
            Pages = pages,
            PendingRecount = index < _pendingRecount.Length && _pendingRecount[index]
        }).ToArray();
}
