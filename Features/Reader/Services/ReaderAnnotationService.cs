using BookHeaven.Core.Entities;
using BookHeaven.Core.Extensions;
using BookHeaven.Core.Features.BookAnnotations;
using BookHeaven.Core.Features.Reader.Abstractions;
using BookHeaven.Core.Shared;
using Mediator;

namespace BookHeaven.Core.Features.Reader.Services;

public class ReaderAnnotationService(
    ISender sender,
    IPendingBookAnnotationDeletionStore pendingDeletionStore) : IReaderAnnotationService
{
    public event Action? OnHighlightsChanged;

    private bool _isInitialized;
    private int _chapterIndex;
    private string _chapterTitle = string.Empty;
    private List<BookAnnotation> _annotations = [];
    private Guid _bookId;
    private Guid _profileId;

    public async Task<Result> InitializeAsync(Guid bookId, Guid profileId)
    {
        _bookId = bookId;
        _profileId = profileId;
        _isInitialized = true;

        var getAnnotations = await sender.Send(new GetBookAnnotations.Query(bookId, profileId));
        if (getAnnotations.IsFailure)
        {
            return Result.Failure(getAnnotations.Error);
        }

        _annotations = getAnnotations.Value;
        return Result.Success();
    }

    public void SetChapter(int chapterIndex, string chapterTitle)
    {
        _chapterIndex = chapterIndex;
        _chapterTitle = chapterTitle;
    }

    public async Task<Result> AddAnnotationAsync(int startOffset, int endOffset, string text)
    {
        if (!_isInitialized || endOffset <= startOffset)
        {
            return Result.Success();
        }

        var annotations = _annotations.ToList();
        var overlapping = annotations
            .Where(annotation => annotation.ChapterIndex == _chapterIndex
                                 && annotation.Overlaps(startOffset, endOffset))
            .ToList();

        if (overlapping.Count == 0)
        {
            annotations.Add(CreateAnnotation(_chapterIndex, _chapterTitle, startOffset, endOffset, text));
        }
        else
        {
            var merged = MergeAnnotationContent(overlapping, startOffset, endOffset, text, null);
            annotations.RemoveAll(annotation => overlapping.Contains(annotation));
            annotations.Add(CreateAnnotation(
                _chapterIndex,
                _chapterTitle,
                merged.StartOffset,
                merged.EndOffset,
                merged.Text,
                merged.Note));
        }

        return await SaveAnnotationChangesAsync(annotations);
    }

    public async Task<Result> CombineAdjacentAnnotationsAsync(int startOffset, int endOffset, string text)
    {
        if (!_isInitialized || endOffset <= startOffset || !HasAdjacentAnnotations(startOffset, endOffset))
        {
            return Result.Success();
        }

        List<BookAnnotation> annotationsToCombine = [];
        var combinedStart = startOffset;
        var combinedEnd = endOffset;
        var expandedRange = true;
        while (expandedRange)
        {
            expandedRange = false;
            foreach (var annotation in _annotations)
            {
                if (annotation.ChapterIndex != _chapterIndex ||
                    annotationsToCombine.Contains(annotation) ||
                    annotation.StartOffset > combinedEnd ||
                    annotation.EndOffset < combinedStart)
                {
                    continue;
                }

                annotationsToCombine.Add(annotation);
                combinedStart = Math.Min(combinedStart, annotation.StartOffset);
                combinedEnd = Math.Max(combinedEnd, annotation.EndOffset);
                expandedRange = true;
            }
        }

        var merged = MergeAnnotationContent(annotationsToCombine, startOffset, endOffset, text, null);
        var retainedAnnotation = annotationsToCombine
            .OrderBy(annotation => annotation.StartOffset)
            .First()
            .Clone();
        retainedAnnotation.StartOffset = merged.StartOffset;
        retainedAnnotation.EndOffset = merged.EndOffset;
        retainedAnnotation.Text = merged.Text;
        retainedAnnotation.Note = merged.Note;

        var annotations = _annotations
            .Where(annotation => !annotationsToCombine.Contains(annotation))
            .Select(annotation => annotation.Clone())
            .ToList();
        annotations.Add(retainedAnnotation);
        return await SaveAnnotationChangesAsync(annotations);
    }

    public async Task<Result> RemoveAnnotationsAsync(int startOffset, int endOffset)
    {
        if (!_isInitialized || endOffset <= startOffset)
        {
            return Result.Success();
        }

        var annotations = _annotations.ToList();
        var changed = annotations.Any(annotation =>
            annotation.ChapterIndex == _chapterIndex
            && CanRemoveWithoutSplitting(annotation, startOffset, endOffset));
        if (!changed) return Result.Success();

        SubtractRange(annotations, _chapterIndex, startOffset, endOffset);
        return await SaveAnnotationChangesAsync(annotations);
    }

    public async Task<Result> UpdateAnnotationRangeAsync(BookAnnotation annotation)
    {
        if (!_isInitialized || annotation.EndOffset <= annotation.StartOffset)
        {
            return Result.Failure(new Error("Invalid highlight range"));
        }

        var existing = _annotations.FirstOrDefault(item =>
            item.BookAnnotationId == annotation.BookAnnotationId);
        if (existing is null || existing.ChapterIndex != annotation.ChapterIndex)
        {
            return Result.Failure(new Error("The book annotation could not be found"));
        }

        annotation.BookId = _bookId;
        annotation.ProfileId = _profileId;
        annotation.NormalizeNote();
        var overlapping = _annotations
            .Where(item => item.BookAnnotationId != annotation.BookAnnotationId
                           && item.ChapterIndex == annotation.ChapterIndex
                           && item.Overlaps(annotation.StartOffset, annotation.EndOffset))
            .ToList();
        var updatedAnnotation = annotation.Clone();
        if (overlapping.Count > 0)
        {
            var merged = MergeAnnotationContent(
                overlapping,
                annotation.StartOffset,
                annotation.EndOffset,
                annotation.Text,
                annotation.Note);
            updatedAnnotation.StartOffset = merged.StartOffset;
            updatedAnnotation.EndOffset = merged.EndOffset;
            updatedAnnotation.Text = merged.Text;
            updatedAnnotation.Note = merged.Note;
        }

        var annotations = _annotations
            .Where(item => item.BookAnnotationId != annotation.BookAnnotationId
                           && !overlapping.Contains(item))
            .Select(item => item.Clone())
            .ToList();
        annotations.Add(updatedAnnotation);
        return await SaveAnnotationChangesAsync(annotations);
    }

    public async Task<Result> SaveAnnotationAsync(BookAnnotation annotation)
    {
        annotation.BookId = _bookId;
        annotation.ProfileId = _profileId;
        annotation.NormalizeNote();

        var result = await sender.Send(new SaveBookAnnotation.Command(annotation));
        if (result.IsFailure) return Result.Failure(result.Error);

        var annotationIndex = _annotations.FindIndex(item => item.BookAnnotationId == annotation.BookAnnotationId);
        if (annotationIndex < 0)
        {
            _annotations.Add(annotation);
        }
        else
        {
            _annotations[annotationIndex] = annotation;
        }

        OnHighlightsChanged?.Invoke();
        return Result.Success();
    }

    public async Task<Result> DeleteAnnotationAsync(Guid annotationId)
    {
        var result = await sender.Send(new DeleteBookAnnotation.Command(_bookId, _profileId, annotationId));
        if (result.IsFailure) return Result.Failure(result.Error);

        await pendingDeletionStore.QueueAsync(_bookId, _profileId, [annotationId]);
        _annotations.RemoveAll(annotation => annotation.BookAnnotationId == annotationId);
        OnHighlightsChanged?.Invoke();
        return Result.Success();
    }

    public BookAnnotation? GetAnnotationForRange(int startOffset, int endOffset)
    {
        if (!_isInitialized) return null;
        return _annotations.FirstOrDefault(annotation =>
            annotation.ChapterIndex == _chapterIndex
            && annotation.Overlaps(startOffset, endOffset));
    }

    public bool HasAnnotationsInRange(int startOffset, int endOffset)
    {
        if (!_isInitialized) return false;

        return _annotations.Any(annotation =>
            annotation.ChapterIndex == _chapterIndex
            && annotation.Overlaps(startOffset, endOffset));
    }

    public bool HasAdjacentAnnotations(int startOffset, int endOffset)
    {
        if (!_isInitialized || endOffset <= startOffset) return false;

        return _annotations.Any(annotation =>
            annotation.ChapterIndex == _chapterIndex
            && (annotation.EndOffset == startOffset || annotation.StartOffset == endOffset));
    }

    public bool HasRemovableAnnotationsInRange(int startOffset, int endOffset)
    {
        if (!_isInitialized || endOffset <= startOffset) return false;

        return _annotations.Any(annotation =>
            annotation.ChapterIndex == _chapterIndex
            && CanRemoveWithoutSplitting(annotation, startOffset, endOffset));
    }

    public bool HasUnhighlightedInRange(int startOffset, int endOffset)
    {
        if (!_isInitialized || endOffset <= startOffset) return false;

        // Highlights never overlap, so a single sweep tells us whether the
        // range is fully covered or has a gap.
        var coveredEnd = startOffset;
        foreach (var annotation in _annotations
            .Where(annotation => annotation.ChapterIndex == _chapterIndex
                                 && annotation.Overlaps(startOffset, endOffset))
            .OrderBy(annotation => annotation.StartOffset))
        {
            if (annotation.StartOffset > coveredEnd) return true;
            coveredEnd = Math.Max(coveredEnd, annotation.EndOffset);
            if (coveredEnd >= endOffset) return false;
        }
        return coveredEnd < endOffset;
    }

    public IReadOnlyList<BookAnnotation> GetAnnotationsForCurrentChapter()
    {
        return _isInitialized ? _annotations.Where(annotation => annotation.ChapterIndex == _chapterIndex).ToList() : [];
    }

    public IReadOnlyList<BookAnnotation> GetAllAnnotations() =>
        _annotations.OrderBy(annotation => annotation.ChapterIndex)
            .ThenBy(annotation => annotation.StartOffset)
            .ToList();

    public void Clear()
    {
        _isInitialized = false;
        _chapterIndex = 0;
        _chapterTitle = string.Empty;
        _bookId = Guid.Empty;
        _profileId = Guid.Empty;
        _annotations = [];
    }

    private async Task<Result> SaveAnnotationChangesAsync(List<BookAnnotation> annotations)
    {
        var existingById = _annotations
            .Where(annotation => annotation.BookAnnotationId != Guid.Empty)
            .ToDictionary(annotation => annotation.BookAnnotationId);
        var requestedById = annotations
            .Where(annotation => annotation.BookAnnotationId != Guid.Empty)
            .ToDictionary(annotation => annotation.BookAnnotationId);
        var upserts = annotations
            .Where(annotation => annotation.BookAnnotationId == Guid.Empty
                                 || !existingById.TryGetValue(annotation.BookAnnotationId, out var existing)
                                 || existing.HasContentChanged(annotation))
            .ToList();
        var deletedAnnotationIds = _annotations
            .Where(annotation => !requestedById.ContainsKey(annotation.BookAnnotationId))
            .Select(annotation => annotation.BookAnnotationId)
            .ToList();

        if (upserts.Count == 0 && deletedAnnotationIds.Count == 0)
        {
            return Result.Success();
        }

        var result = await sender.Send(new ApplyBookAnnotationChanges.Command(
            _bookId,
            _profileId,
            upserts,
            deletedAnnotationIds));
        if (result.IsFailure) return Result.Failure(result.Error);

        await pendingDeletionStore.QueueAsync(_bookId, _profileId, deletedAnnotationIds);
        _annotations = annotations;
        OnHighlightsChanged?.Invoke();
        return Result.Success();
    }

    private BookAnnotation CreateAnnotation(
        int chapterIndex,
        string chapterTitle,
        int startOffset,
        int endOffset,
        string text,
        string? note = null) =>
        new()
        {
            BookId = _bookId,
            ProfileId = _profileId,
            ChapterIndex = chapterIndex,
            ChapterTitle = chapterTitle,
            StartOffset = startOffset,
            EndOffset = endOffset,
            Text = text,
            Note = note
        };

    private static (int StartOffset, int EndOffset, string Text, string? Note) MergeAnnotationContent(
        IReadOnlyCollection<BookAnnotation> overlapping,
        int startOffset,
        int endOffset,
        string text,
        string? note)
    {
        var mergedStart = Math.Min(startOffset, overlapping.Min(annotation => annotation.StartOffset));
        var mergedEnd = Math.Max(endOffset, overlapping.Max(annotation => annotation.EndOffset));
        var mergedText = new char[mergedEnd - mergedStart];
        foreach (var annotation in overlapping)
        {
            var index = annotation.StartOffset - mergedStart;
            annotation.Text.CopyTo(0, mergedText, index, Math.Min(annotation.Text.Length, mergedText.Length - index));
        }

        var newIndex = startOffset - mergedStart;
        text.CopyTo(0, mergedText, newIndex, Math.Min(text.Length, mergedText.Length - newIndex));
        var notes = new[] { note }
            .Concat(overlapping.Select(annotation => annotation.Note))
            .Where(existingNote => !string.IsNullOrWhiteSpace(existingNote))
            .Distinct()
            .ToList();
        return (
            mergedStart,
            mergedEnd,
            new string(mergedText),
            notes.Count == 0 ? null : string.Join(Environment.NewLine + Environment.NewLine, notes));
    }

    // Removes edge overlaps without splitting a highlight into two separate annotations.
    private static void SubtractRange(List<BookAnnotation> annotations, int chapterIndex, int startOffset, int endOffset)
    {
        var overlapping = annotations
            .Where(annotation => annotation.ChapterIndex == chapterIndex
                                 && CanRemoveWithoutSplitting(annotation, startOffset, endOffset))
            .ToList();

        foreach (var annotation in overlapping)
        {
            annotations.Remove(annotation);
            var noteAssigned = false;
            if (annotation.StartOffset < startOffset)
            {
                annotations.Add(annotation.WithRange(
                    endOffset: startOffset,
                    text: annotation.Text[..Math.Min(startOffset - annotation.StartOffset, annotation.Text.Length)]));
                noteAssigned = true;
            }
            if (annotation.EndOffset > endOffset)
            {
                var remainder = annotation.WithRange(
                    startOffset: endOffset,
                    text: annotation.Text[Math.Min(endOffset - annotation.StartOffset, annotation.Text.Length)..]);
                if (noteAssigned)
                {
                    remainder.Note = null;
                    remainder.BookAnnotationId = Guid.Empty;
                }
                annotations.Add(remainder);
            }
        }
    }

    private static bool CanRemoveWithoutSplitting(BookAnnotation annotation, int startOffset, int endOffset) =>
        annotation.Overlaps(startOffset, endOffset)
        && (startOffset <= annotation.StartOffset || endOffset >= annotation.EndOffset);
}
