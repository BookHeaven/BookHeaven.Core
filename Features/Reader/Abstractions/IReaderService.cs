using BookHeaven.Core.DOM.Text.Enums;
using BookHeaven.Core.Entities;
using BookHeaven.Core.Features.Reader.Models;
using BookHeaven.EbookManager.Entities;

namespace BookHeaven.Core.Features.Reader.Abstractions;

public interface IReaderService : IDisposable
{
    bool IsReady { get; }
    bool IsCalculatingPages { get; }
    ReaderState? State { get; }
    ProfileSettings? Settings { get; }
    event Action? OnPageChanged;
    event Action? OnChapterChanged;
    event Action? OnTotalPagesChanged;
    Task<Result> InitializeAsync(Guid bookId, Guid profileId, ReaderContext? context = null);
    void PauseTimer();
    void ResumeTimer();
    void SetTotalPages(int[] pageArray);
    Task CalculatePagesAsync(ReaderContext context);
    void NavigateToInitialPage();
    void NavigateToChapter(TocEntry chapter);
    void NextPage();
    void PreviousPage();
    void NextChapter();
    void PreviousChapter();
    Task SaveProgressAsync();
}