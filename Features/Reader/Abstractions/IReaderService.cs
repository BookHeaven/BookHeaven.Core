using BookHeaven.Core.Entities;
using BookHeaven.Core.Features.Reader.Models;
using BookHeaven.EbookManager.Entities;

namespace BookHeaven.Core.Features.Reader.Abstractions;

public interface IReaderService : IDisposable
{
    ReaderState? State { get; }
    bool IsInitialLoadReady { get; }
    bool IsCurrentChapterLoaded { get; }
    bool IsCalculatingPages { get; }
    ProfileSettings? Settings { get; }
    event Action? OnPageChanged;
    event Action? OnChapterChanged;
    event Action? OnTotalPagesChanged;
    Task<Result> InitializeAsync(Guid bookId, Guid profileId, IReaderViewAdapter viewAdapter);
    Task<bool> AdvanceViewAsync();
    void RequestPageRecount();
    void PauseTimer();
    void ResumeTimer();
    void NavigateToChapter(TocEntry chapter);
    void NextPage();
    void PreviousPage();
    void NextChapter();
    void PreviousChapter();
    Task SaveProgressAsync();
    bool NavigateToAnnotation(BookAnnotation annotation);
    void NavigateToPage(int pageNumber);
}