using BookHeaven.Core.Entities;
using BookHeaven.Core.Features.Reader.Models;
using BookHeaven.EbookManager.Entities;

namespace BookHeaven.Core.Features.Reader.Abstractions;

public interface IReaderService : IDisposable
{
    bool IsReady { get; }
    ReaderState? State { get; }
    ProfileSettings? Settings { get; }
    event Action? OnPageChanged;
    event Action? OnChapterChanged;
    event Action? OnTotalPagesChanged;
    Task<Result> InitializeAsync(Guid bookId, Guid profileId);
    Task LoadCurrentContentAsync();
    void SetTotalPages(int totalPages, int? totalPagesPrev = null, int? totalPagesNext = null);
    void NavigateTo(int page, int chapter);
    void NavigateToInitialPage();
    void NavigateToChapter(TocEntry chapter);
    void NextPage();
    void PreviousPage();
    Task SaveProgressAsync();
}