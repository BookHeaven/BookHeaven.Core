using BookHeaven.Core.Entities;
using BookHeaven.Core.Shared;

namespace BookHeaven.Core.Features.Reader.Abstractions;

public interface IReaderAnnotationService
{
    event Action? OnHighlightsChanged;

    Task<Result> InitializeAsync(Guid bookId, Guid profileId);
    void SetChapter(int chapterIndex, string chapterTitle);
    Task<Result> AddAnnotationAsync(int startOffset, int endOffset, string text);
    Task<Result> CombineAdjacentAnnotationsAsync(int startOffset, int endOffset, string text);
    Task<Result> RemoveAnnotationsAsync(int startOffset, int endOffset);
    Task<Result> UpdateAnnotationRangeAsync(BookAnnotation annotation);
    Task<Result> SaveAnnotationAsync(BookAnnotation annotation);
    Task<Result> DeleteAnnotationAsync(Guid annotationId);
    BookAnnotation? GetAnnotationForRange(int startOffset, int endOffset);
    bool HasAnnotationsInRange(int startOffset, int endOffset);
    bool HasAdjacentAnnotations(int startOffset, int endOffset);
    bool HasRemovableAnnotationsInRange(int startOffset, int endOffset);
    bool HasUnhighlightedInRange(int startOffset, int endOffset);
    IReadOnlyList<BookAnnotation> GetAnnotationsForCurrentChapter();
    IReadOnlyList<BookAnnotation> GetAllAnnotations();
    void Clear();
}
