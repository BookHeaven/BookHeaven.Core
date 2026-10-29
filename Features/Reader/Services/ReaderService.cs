using BookHeaven.Core.Abstractions.Services;
using BookHeaven.Core.DOM.Services.Abstractions;
using BookHeaven.Core.DOM.Text.Enums;
using BookHeaven.Core.Entities;
using BookHeaven.Core.Extensions;
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

    private static readonly TimeSpan SaveProgressInterval = TimeSpan.FromSeconds(5);
    
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
    
    public void Dispose()
    {
        IsReady = false;
        State = null;
        _bookId = Guid.Empty;
        _profileId = Guid.Empty;
        _progress = null;
        // Waiting here also guarantees an in-flight save (whose state was already
        // captured locally) completes before the service is torn down.
        _saveTimer?.Dispose();
        _saveTimer = null;
        _calcCts?.Cancel();
        readerSettingsService.Dispose();
        GC.SuppressFinalize(this);
    }
}