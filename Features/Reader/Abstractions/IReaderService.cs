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
    event Action? OnHighlightsChanged;
    Task<Result> InitializeAsync(Guid bookId, Guid profileId);
    Task LoadCachedPagesAsync(ReaderContext context);
    void PauseTimer();
    void ResumeTimer();
    Task CalculatePagesAsync(ReaderContext context);
    void NavigateToInitialPage();
    void NavigateToChapter(TocEntry chapter);
    void NextPage();
    void PreviousPage();
    void NextChapter();
    void PreviousChapter();
    Task SaveProgressAsync();
    Task<Result> AddHighlightAsync(int startOffset, int endOffset, string text);
    Task<Result> RemoveHighlightsAsync(int startOffset, int endOffset);
    Task<Result> SaveAnnotationNoteAsync(int startOffset, int endOffset, string text, string? note);
    Task<Result> UpdateAnnotationNoteAsync(Guid annotationId, string? note);
    Task<Result> DeleteAnnotationAsync(Guid annotationId);
    BookAnnotation? GetAnnotationForRange(int startOffset, int endOffset);
    bool HasHighlightsInRange(int startOffset, int endOffset);
    bool HasUnhighlightedInRange(int startOffset, int endOffset);
    IReadOnlyList<BookAnnotation> GetAnnotationsForCurrentChapter();
    IReadOnlyList<BookAnnotation> GetAllAnnotations();
    bool NavigateToAnnotation(BookAnnotation annotation);
    void NavigateToPage(int pageNumber);
}