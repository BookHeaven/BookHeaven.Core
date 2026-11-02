using BookHeaven.Core.Abstractions.Services;
using BookHeaven.Core.DOM.Services.Abstractions;
using BookHeaven.Core.Entities;
using BookHeaven.Core.Extensions;
using BookHeaven.Core.Features.BookAnnotations;
using BookHeaven.Core.Features.Books;
using BookHeaven.Core.Features.BooksProgress;
using BookHeaven.Core.Features.Reader.Abstractions;
using BookHeaven.Core.Features.Reader.Extensions;
using BookHeaven.Core.Features.Reader.Models;
using BookHeaven.EbookManager.Abstractions;
using BookHeaven.EbookManager.Entities;
using BookHeaven.EbookManager.Enums;
using Mediator;

namespace BookHeaven.Core.Features.Reader.Services;

public class ReaderService(
    IUrlBuilder urlBuilder,
    IReaderSettingsService readerSettingsService,
    IEbookManagerProvider ebookManagerProvider,
    IReaderCacheService readerCacheService,
    IPageCalculator pageCalculator,
    ISender sender,
    TimeProvider timeProvider) : IReaderService
{
    public bool IsReady { get; private set; }
    public bool IsCalculatingPages { get; private set; }
    
    public ReaderState? State { get; private set; }
    public ProfileSettings? Settings => readerSettingsService.ReaderSettings;

    public event Action? OnPageChanged;
    public event Action? OnChapterChanged;
    public event Action? OnTotalPagesChanged;
    public event Action? OnHighlightsChanged;

    private static readonly TimeSpan SaveProgressInterval = TimeSpan.FromSeconds(5);
    private List<BookAnnotation> _annotations = [];

    private Guid _bookId;
    private Guid _profileId;
    private BookProgress? _progress;
    private ITimer? _saveTimer;
    private bool _isDirty;
    private int _isSaving;

    // Tracks the in-flight page calculation so a new call can cancel the previous
    // one. Each calculation owns its CTS and disposes it in its own finally block;
    // this field is only ever null or a live (non-disposed) source.
    private CancellationTokenSource? _calcCts;
    
    public async Task<Result> InitializeAsync(Guid bookId, Guid profileId)
    {
        _bookId = bookId;
        _profileId = profileId;
        State = new(timeProvider);
        await readerSettingsService.LoadSettings(profileId);

        var getBook = await sender.Send(new GetBook.Query { BookId = bookId });
        if(getBook.IsFailure)
        {
            return Result.Failure(getBook.Error);
        }
        var book = getBook.Value;

        var getProgress = await sender.Send(new GetBookProgressByProfile.Query(bookId, profileId));
        if(getProgress.IsFailure)
        {
            return Result.Failure(getProgress.Error);
        }
        _progress = getProgress.Value;

        var getAnnotations = await sender.Send(new GetBookAnnotations.Query(bookId, profileId));
        if (getAnnotations.IsFailure)
        {
            return Result.Failure(getAnnotations.Error);
        }
        _annotations = getAnnotations.Value;

        // Debounced auto-save: a navigation marks the progress dirty and the timer
        // persists it at most once per interval, so rapid page turns don't hit the
        // database on every turn.
        if(_saveTimer is not null) await _saveTimer.DisposeAsync();
        _saveTimer = timeProvider.CreateTimer(timer =>
        {
            if (_isDirty && _isSaving == 0)
            {
                _ = SaveProgressAsync();
            }
        }, null, SaveProgressInterval, SaveProgressInterval);

        State.SetEbook(await LoadEbookAsync(book));
        return Result.Success();
    }
    
    public async Task LoadCachedPagesAsync(ReaderContext readerContext)
    {
        if (State is null || Settings is null) return;
        var options = Settings.ToPageCalculatorOptions(readerContext);
        var cachedPages = await readerCacheService.LoadCachedPagesAsync(_bookId, options.CalculateHash());
        if (cachedPages.Length > 0)
        {
            State.SetPagesPerChapter(cachedPages);
        }
    }
    
    private async Task<Ebook> LoadEbookAsync(Book book)
    {
        var reader = ebookManagerProvider.GetReader((Format)book.Format);
        Ebook ebook;
        var content = await readerCacheService.LoadCachedContentAsync(_bookId);
        var ebookFilePath = urlBuilder.EbookFilePath(book.BookId, book.Format);
        if (content is null)
        {
            ebook = await reader.ReadAllAsync(ebookFilePath);
            _ = readerCacheService.CacheContentAsync(_bookId, ebook.Content);
        }
        else
        {
            ebook = await reader.ReadMetadataAsync(ebookFilePath);
            ebook.Content = content;
        }
        return ebook;
    }
    
    public void PauseTimer()
    {
        State?.PauseTimer();
    }
    
    public void ResumeTimer()
    {
        State?.ResumeTimer();
    }
    
    public async Task CalculatePagesAsync(ReaderContext readerContext)
    {
        if (State?.Ebook is null || Settings is null) return;

        IsCalculatingPages = true;
        // Supersede any in-flight calculation: cancel it so it stops ASAP and its
        // partial result is dropped, then start a fresh one with a new token.
        if (_calcCts is not null)
        {
            await _calcCts.CancelAsync();
        }
        var cts = new CancellationTokenSource();
        _calcCts = cts;

        var options = Settings.ToPageCalculatorOptions(readerContext);
        try
        {
            var pages = await pageCalculator.CalculatePagesAsync(State.Ebook, options, cts.Token);
            var pageCounts = ToPagesPerChapter(pages, options.ColumnCount);
            State.SetPagesPerChapter(pageCounts);
            OnTotalPagesChanged?.Invoke();
            _ = readerCacheService.SaveCachedPagesAsync(_bookId, _profileId, options.CalculateHash(), State.PagesPerChapter);
        }
        catch (OperationCanceledException)
        {
            // A newer calculation superseded this one; drop the partial result.
        }
        finally
        {
            // Clear the field before disposing so no other thread ever sees a
            // disposed source; only the current owner clears it.
            if (ReferenceEquals(_calcCts, cts))
                _calcCts = null;
            cts.Dispose();
            IsCalculatingPages = false;
        }
    }

    private static int[] ToPagesPerChapter(int[] columnCounts, int columnCount)
    {
        return columnCount <= 1 ? columnCounts : columnCounts.Select(columns => (columns + columnCount - 1) / columnCount).ToArray();
    }
    
    private void NavigateTo(int page, int chapter)
    {
        if(State is null) return;
        State.SetChapterAndPage(chapter, page);
        MarkDirty();
        OnChapterChanged?.Invoke();
    }
    
    private void MarkDirty()
    {
        _isDirty = true;
    }
    
    public void NavigateToInitialPage()
    {
        if(State is null) return;
        var targetChapter = _progress?.Chapter ?? 0;
        var targetPage = 1;
        var chapterPageCount = State.PagesPerChapter.ElementAtOrDefault(targetChapter);
        
        targetChapter = Math.Clamp(targetChapter, 0, State.TotalChapters - 1);
        if (_progress?.ChapterProgress > 0)
        {
            targetPage = (int)Math.Round(_progress.ChapterProgress * chapterPageCount);
        }
        if (chapterPageCount > 0)
        {
            targetPage = Math.Clamp(targetPage, 1, chapterPageCount);
        }
        _progress?.StartDate ??= DateTimeOffset.Now;
        NavigateTo(targetPage, targetChapter);
        State.StartTimer();
        IsReady = true;
    }
    
    public void NavigateToChapter(TocEntry tocEntry)
    {
        if(State is null) return;
        var chapter = State?.Ebook!.Content.Chapters.Index().FirstOrDefault(i => i.Item.Identifier == tocEntry.Id);
        if(chapter?.Item is null) return;
        NavigateTo(1, chapter.Value.Index);
    }

    public void NextPage()
    {
        if (State is null || State.TotalPages <= 0) return;
        if (State.PageNumber < State.TotalPages)
        {
            State.SetChapterAndPage(State.ChapterNumber, State.PageNumber + 1);
            MarkDirty();
            OnPageChanged?.Invoke();
            return;
        }
        
        if(State.ChapterNumber == State.TotalChapters - 1) return;
            
        State.SetChapterAndPage(State.ChapterNumber + 1, 1);
        MarkDirty();
        OnChapterChanged?.Invoke();
        OnTotalPagesChanged?.Invoke();
    }
    
    public void PreviousPage()
    {
        if (State is null || State.TotalPages <= 0) return;
        if (State.PageNumber > 1)
        {
            State.SetChapterAndPage(State.ChapterNumber, State.PageNumber - 1);
            MarkDirty();
            OnPageChanged?.Invoke();
            return;
        }
        
        if(State.ChapterNumber == 0) return;
            
        State.SetChapterAndPage(State.ChapterNumber - 1, State.PagesPerChapter.ElementAtOrDefault(State.ChapterNumber - 1));
        MarkDirty();
        OnChapterChanged?.Invoke();
        OnTotalPagesChanged?.Invoke();
    }
    
    public void NextChapter()
    {
        if (State is null) return;
        if (State.ChapterNumber >= State.TotalChapters - 1) return;
        State.SetChapterAndPage(State.ChapterNumber + 1, 1);
        MarkDirty();
        OnChapterChanged?.Invoke();
    }
    
    public void PreviousChapter()
    {
        if (State is null) return;
        if (State.ChapterNumber <= 0) return;
        State.SetChapterAndPage(State.ChapterNumber - 1, 1);
        MarkDirty();
        OnChapterChanged?.Invoke();
    }
    
    public async Task SaveProgressAsync()
    {
        var state = State;
        var progress = _progress;
        if (state is null || progress is null || state.TotalPages == 0) return;
        // Only one save may be in flight; a concurrent call is dropped because the
        // in-flight save already reflects the latest navigation.
        if (Interlocked.Exchange(ref _isSaving, 1) == 1) return;
        try
        {
            progress.Chapter = state.ChapterNumber;
            progress.ChapterProgress = (double)state.ChapterProgress;

            if (progress.EndDate is null)
            {
                progress.Progress = state.ProgressAsPercentage;
                progress.ElapsedTime += state.GetElapsedReadingTime();
                progress.LastRead = DateTimeOffset.Now;

                if (state.ChapterNumber == state.Ebook!.Content.Chapters.Count - 1 &&
                    state.PageNumber == state.TotalPages)
                {
                    progress.EndDate = DateTimeOffset.Now;
                }
            }

            await sender.Send(new UpdateBookProgress.Command(progress));
            _isDirty = false;
        }
        finally
        {
            Interlocked.Exchange(ref _isSaving, 0);
        }
    }
    
    public async Task<Result> AddAnnotationAsync(int startOffset, int endOffset, string text)
    {
        if (State is null || endOffset <= startOffset)
        {
            return Result.Success();
        }

        var annotations = _annotations.ToList();
        var overlapping = annotations
            .Where(annotation => annotation.ChapterIndex == State.ChapterNumber
                                 && annotation.StartOffset < endOffset
                                 && annotation.EndOffset > startOffset)
            .ToList();

        if (overlapping.Count == 0)
        {
            annotations.Add(CreateAnnotation(State.ChapterNumber, State.ChapterTitle, startOffset, endOffset, text));
        }
        else
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
            var notes = overlapping
                .Select(annotation => annotation.Note)
                .Where(note => !string.IsNullOrWhiteSpace(note))
                .Distinct()
                .ToList();
            annotations.RemoveAll(annotation => overlapping.Contains(annotation));
            annotations.Add(CreateAnnotation(
                State.ChapterNumber,
                State.ChapterTitle,
                mergedStart,
                mergedEnd,
                new string(mergedText),
                notes.Count == 0 ? null : string.Join(Environment.NewLine + Environment.NewLine, notes)));
        }

        return await SaveAnnotationChangesAsync(annotations);
    }

    public async Task<Result> RemoveAnnotationsAsync(int startOffset, int endOffset)
    {
        if (State is null  || endOffset <= startOffset)
        {
            return Result.Success();
        }

        var annotations = _annotations.ToList();
        var changed = annotations.Any(annotation =>
            annotation.ChapterIndex == State.ChapterNumber
            && annotation.StartOffset < endOffset
            && annotation.EndOffset > startOffset);
        if (!changed) return Result.Success();

        SubtractRange(annotations, State.ChapterNumber, startOffset, endOffset);
        return await SaveAnnotationChangesAsync(annotations);
    }

    public async Task<Result> SaveAnnotationAsync(BookAnnotation annotation)
    {
        annotation.BookId = _bookId;
        annotation.ProfileId = _profileId;
        annotation.NormalizeNote();

        var result = await sender.Send(new SaveBookAnnotation.Command(annotation));
        if (result.IsFailure) return Result.Failure(result.Error);
        
        var annotationIndex = _annotations.FindIndex(
            item => item.BookAnnotationId == annotation.BookAnnotationId);
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

        _annotations.RemoveAll(annotation => annotation.BookAnnotationId == annotationId);
        OnHighlightsChanged?.Invoke();
        return Result.Success();
    }

    public BookAnnotation? GetAnnotationForRange(int startOffset, int endOffset)
    {
        if (State is null) return null;
        return _annotations.FirstOrDefault(annotation =>
            annotation.ChapterIndex == State.ChapterNumber
            && annotation.StartOffset < endOffset
            && annotation.EndOffset > startOffset);
    }

    public bool HasAnnotationsInRange(int startOffset, int endOffset)
    {
        if (State is null) return false;

        return _annotations.Any(annotation =>
            annotation.ChapterIndex == State.ChapterNumber
            && annotation.StartOffset < endOffset
            && annotation.EndOffset > startOffset);
    }

    public bool HasUnhighlightedInRange(int startOffset, int endOffset)
    {
        if (State is null || endOffset <= startOffset) return false;

        // Highlights never overlap, so a single sweep tells us whether the
        // range is fully covered or has a gap.
        var coveredEnd = startOffset;
        foreach (var annotation in _annotations
            .Where(annotation => annotation.ChapterIndex == State.ChapterNumber
                                 && annotation.EndOffset > startOffset
                                 && annotation.StartOffset < endOffset)
            .OrderBy(annotation => annotation.StartOffset))
        {
            if (annotation.StartOffset > coveredEnd) return true;
            coveredEnd = Math.Max(coveredEnd, annotation.EndOffset);
            if (coveredEnd >= endOffset) return false;
        }
        return coveredEnd < endOffset;
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

    private static BookAnnotation CopyAnnotation(
        BookAnnotation source,
        int? startOffset = null,
        int? endOffset = null,
        string? text = null,
        string? note = null,
        bool replaceNote = false,
        Guid? annotationId = null) =>
        new()
        {
            BookAnnotationId = annotationId ?? source.BookAnnotationId,
            BookId = source.BookId,
            ProfileId = source.ProfileId,
            ChapterIndex = source.ChapterIndex,
            ChapterTitle = source.ChapterTitle,
            StartOffset = startOffset ?? source.StartOffset,
            EndOffset = endOffset ?? source.EndOffset,
            Text = text ?? source.Text,
            Note = replaceNote ? note : source.Note
        };

    // Removes the selected range from the affected annotations, keeping their non-overlapping remainders.
    private static void SubtractRange(List<BookAnnotation> annotations, int chapterIndex, int startOffset, int endOffset)
    {
        var overlapping = annotations
            .Where(annotation => annotation.ChapterIndex == chapterIndex
                                && annotation.StartOffset < endOffset
                                && annotation.EndOffset > startOffset)
            .ToList();

        foreach (var annotation in overlapping)
        {
            annotations.Remove(annotation);
            var noteAssigned = false;
            if (annotation.StartOffset < startOffset)
            {
                annotations.Add(CopyAnnotation(
                    annotation,
                    endOffset: startOffset,
                    text: annotation.Text[..Math.Min(startOffset - annotation.StartOffset, annotation.Text.Length)]));
                noteAssigned = true;
            }
            if (annotation.EndOffset > endOffset)
            {
                annotations.Add(CopyAnnotation(
                    annotation,
                    startOffset: endOffset,
                    text: annotation.Text[Math.Min(endOffset - annotation.StartOffset, annotation.Text.Length)..],
                    note: noteAssigned ? null : annotation.Note,
                    replaceNote: true,
                    annotationId: noteAssigned ? Guid.Empty : null));
            }
        }
    }

    public IReadOnlyList<BookAnnotation> GetAnnotationsForCurrentChapter()
    {
        return State is null ? [] : _annotations.Where(annotation => annotation.ChapterIndex == State.ChapterNumber).ToList();
    }

    public IReadOnlyList<BookAnnotation> GetAllAnnotations() =>
        _annotations.OrderBy(annotation => annotation.ChapterIndex)
            .ThenBy(annotation => annotation.StartOffset)
            .ToList();

    public bool NavigateToAnnotation(BookAnnotation annotation)
    {
        var chapters = State?.Ebook?.Content.Chapters;
        if (State is null || chapters is null || chapters.Count == 0) return false;

        var targetChapterIndex = -1;

        if (annotation.ChapterIndex < chapters.Count)
        {
            targetChapterIndex = annotation.ChapterIndex;
        }

        if (targetChapterIndex < 0 || targetChapterIndex >= chapters.Count) return false;

        var chapterChanged = targetChapterIndex != State.ChapterNumber;
        State.SetChapterAndPage(targetChapterIndex, 1);
        MarkDirty();
        if (chapterChanged) OnChapterChanged?.Invoke();
        else OnPageChanged?.Invoke();
        return true;
    }

    public void NavigateToPage(int pageNumber)
    {
        if (State is null) return;

        var pageCount = State.PagesPerChapter.ElementAtOrDefault(State.ChapterNumber);
        var targetPageNumber = Math.Clamp(pageNumber, 1, Math.Max(1, pageCount));
        if (State.PageNumber == targetPageNumber) return;

        State.SetChapterAndPage(State.ChapterNumber, targetPageNumber);
        MarkDirty();
        OnPageChanged?.Invoke();
    }

    public void Dispose()
    {
        IsReady = false;
        State = null;
        _bookId = Guid.Empty;
        _profileId = Guid.Empty;
        _progress = null;
        _annotations = [];
        // Waiting here also guarantees an in-flight save (whose state was already
        // captured locally) completes before the service is torn down.
        _saveTimer?.Dispose();
        _saveTimer = null;
        _calcCts?.Cancel();
        readerSettingsService.Dispose();
        GC.SuppressFinalize(this);
    }
}