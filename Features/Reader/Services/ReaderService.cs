using BookHeaven.Core.Abstractions.Services;
using BookHeaven.Core.DOM.Services.Abstractions;
using BookHeaven.Core.Entities;
using BookHeaven.Core.Extensions;
using BookHeaven.Core.Features.Books;
using BookHeaven.Core.Features.BooksProgress;
using BookHeaven.Core.Features.Reader.Abstractions;
using BookHeaven.Core.Features.Reader.Enums;
using BookHeaven.Core.Features.Reader.Extensions;
using BookHeaven.Core.Features.Reader.Models;
using BookHeaven.EbookManager.Abstractions;
using BookHeaven.EbookManager.Entities;
using BookHeaven.EbookManager.Enums;
using Mediator;

namespace BookHeaven.Core.Features.Reader.Services;

public sealed class ReaderService(
    IUrlBuilder urlBuilder,
    IReaderSettingsService readerSettingsService,
    IEbookManagerProvider ebookManagerProvider,
    IReaderCacheService readerCacheService,
    IPageCalculator pageCalculator,
    ISender sender,
    IReaderAnnotationService annotationService,
    TimeProvider timeProvider) : IReaderService
{
    private ReaderRecountState _recountState = ReaderRecountState.None;

    public ReaderState? State { get; private set; }
    private ReaderFlowState FlowState { get; set; } = ReaderFlowState.Uninitialized;
    private ReaderPageCalculationState PageCalculationState { get; set; } = ReaderPageCalculationState.NotStarted;

    public bool IsInitialLoadReady =>
        FlowState is ReaderFlowState.Ready or ReaderFlowState.WaitingForChapterAssets;
    public bool IsCurrentChapterLoaded => FlowState == ReaderFlowState.Ready;
    public bool IsCalculatingPages => PageCalculationState is
        ReaderPageCalculationState.CalculatingInitialPages or ReaderPageCalculationState.RecalculatingPages;

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

    private CancellationTokenSource? _calcCts;
    private Guid _assetLoadToken = Guid.NewGuid();
    private IReaderViewAdapter? _viewAdapter;
    private int _viewFlowRunning;

    public async Task<Result> InitializeAsync(Guid bookId, Guid profileId, IReaderViewAdapter viewAdapter)
    {
        ArgumentNullException.ThrowIfNull(viewAdapter);

        TransitionFlowTo(ReaderFlowState.Uninitialized);
        TransitionPageCalculationTo(ReaderPageCalculationState.NotStarted);
        TransitionRecountTo(ReaderRecountState.None);
        _viewAdapter = viewAdapter;

        _bookId = bookId;
        _profileId = profileId;
        State = new(timeProvider);
        await readerSettingsService.LoadSettings(profileId);

        var getBook = await sender.Send(new GetBook.Query { BookId = bookId });
        if(getBook.IsFailure)
        {
            TransitionFlowTo(ReaderFlowState.Initialized);
            return Result.Failure(getBook.Error);
        }
        var book = getBook.Value;

        var getProgress = await sender.Send(new GetBookProgressByProfile.Query(bookId, profileId));
        if(getProgress.IsFailure)
        {
            TransitionFlowTo(ReaderFlowState.Initialized);
            return Result.Failure(getProgress.Error);
        }
        _progress = getProgress.Value;

        var getAnnotations = await annotationService.InitializeAsync(bookId, profileId);
        if (getAnnotations.IsFailure)
        {
            TransitionFlowTo(ReaderFlowState.Initialized);
            return Result.Failure(getAnnotations.Error);
        }

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
        TransitionFlowTo(ReaderFlowState.Initialized);
        return Result.Success();
    }

    public async Task<bool> AdvanceViewAsync()
    {
        var viewAdapter = _viewAdapter
            ?? throw new InvalidOperationException("The reader view adapter has not been initialized.");

        if (Interlocked.Exchange(ref _viewFlowRunning, 1) != 0) return false;

        try
        {
            if (FlowState == ReaderFlowState.Initialized)
            {
                var context = await viewAdapter.GetReaderContextAsync();
                await LoadCachedPagesAsync(context);
                if (State?.PagesPerChapter.Length > 0)
                {
                    NavigateToInitialPage();
                }
                else
                {
                    await UpdateTotalPagesAsync(context);
                }

                _assetLoadToken = Guid.NewGuid();
                TransitionFlowTo(ReaderFlowState.WaitingForInitialAssets);
                return true;
            }

            if (IsWaitingForAssets(FlowState))
            {
                var assetLoadToken = _assetLoadToken;
                await viewAdapter.WaitForAssetsToLoadAsync();
                if (assetLoadToken != _assetLoadToken || !IsWaitingForAssets(FlowState))
                {
                    return true;
                }

                if (PageCalculationState != ReaderPageCalculationState.Ready) return true;
                await viewAdapter.RestoreHighlightsAsync();
                if (assetLoadToken != _assetLoadToken || !IsWaitingForAssets(FlowState))
                {
                    return true;
                }

                TransitionFlowTo(ReaderFlowState.Ready);
                return true;
            }

            if (FlowState == ReaderFlowState.Ready && _recountState == ReaderRecountState.Requested)
            {
                TransitionRecountTo(ReaderRecountState.None);
                var calculationCompleted = false;
                try
                {
                    var context = await viewAdapter.GetReaderContextAsync();
                    await UpdateTotalPagesAsync(context);
                    calculationCompleted = true;
                    return true;
                }
                finally
                {
                    if (!calculationCompleted && _recountState == ReaderRecountState.None)
                    {
                        TransitionRecountTo(ReaderRecountState.Requested);
                    }
                }
            }

            return false;
        }
        finally
        {
            Interlocked.Exchange(ref _viewFlowRunning, 0);
        }
    }

    public void RequestPageRecount()
    {
        if (PageCalculationState is
            ReaderPageCalculationState.CalculatingInitialPages or ReaderPageCalculationState.RecalculatingPages)
        {
            TransitionRecountTo(ReaderRecountState.RequestedDuringCalculation);
        }
        else if (_recountState == ReaderRecountState.None)
        {
            TransitionRecountTo(ReaderRecountState.Requested);
        }
    }

    private async Task LoadCachedPagesAsync(ReaderContext readerContext)
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
        _saveTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        State?.PauseTimer();
    }

    public void ResumeTimer()
    {
        _saveTimer?.Change(SaveProgressInterval, SaveProgressInterval);
        State?.ResumeTimer();
    }

    private async Task CalculatePagesAsync(ReaderContext readerContext)
    {
        if (State?.Ebook is null || Settings is null) return;

        var calculationState = PageCalculationState is
            ReaderPageCalculationState.Ready or ReaderPageCalculationState.RecalculatingPages
            ? ReaderPageCalculationState.RecalculatingPages
            : ReaderPageCalculationState.CalculatingInitialPages;
        TransitionPageCalculationTo(calculationState);
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
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // Disposal cancels the active calculation; discard its partial result.
        }
        finally
        {
            if (ReferenceEquals(_calcCts, cts))
                _calcCts = null;
            if (_recountState == ReaderRecountState.RequestedDuringCalculation)
            {
                TransitionRecountTo(ReaderRecountState.Requested);
            }
            TransitionPageCalculationTo(
                FlowState == ReaderFlowState.Uninitialized || calculationState == ReaderPageCalculationState.CalculatingInitialPages
                    ? ReaderPageCalculationState.NotStarted
                    : ReaderPageCalculationState.Ready);
            cts.Dispose();
        }
    }

    private static int[] ToPagesPerChapter(int[] columnCounts, int columnCount)
    {
        return columnCount <= 1 ? columnCounts : columnCounts.Select(columns => (columns + columnCount - 1) / columnCount).ToArray();
    }

    private void NavigateTo(int page, int chapter)
    {
        if(State is null) return;
        ChangeChapter(chapter, page);
    }

    private void ChangeChapter(int chapter, int page)
    {
        State!.SetChapterAndPage(chapter, page);
        annotationService.SetChapter(chapter, State.ChapterTitle);
        MarkDirty();
        if (FlowState is ReaderFlowState.Initialized or ReaderFlowState.WaitingForInitialAssets)
        {
            _assetLoadToken = Guid.NewGuid();
            TransitionFlowTo(ReaderFlowState.WaitingForInitialAssets);
        }
        else if (FlowState is ReaderFlowState.Ready or ReaderFlowState.WaitingForChapterAssets)
        {
            _assetLoadToken = Guid.NewGuid();
            TransitionFlowTo(ReaderFlowState.WaitingForChapterAssets);
        }
        OnChapterChanged?.Invoke();
    }

    private void MarkDirty()
    {
        _isDirty = true;
    }

    private void NavigateToInitialPage()
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
        TransitionPageCalculationTo(ReaderPageCalculationState.Ready);
    }

    private async Task UpdateTotalPagesAsync(ReaderContext context)
    {
        await CalculatePagesAsync(context);
        if (PageCalculationState is
            ReaderPageCalculationState.NotStarted or ReaderPageCalculationState.CalculatingInitialPages)
        {
            NavigateToInitialPage();
        }
    }

    private void TransitionFlowTo(ReaderFlowState nextState)
    {
        if (FlowState == nextState) return;

        var transitionAllowed = nextState == ReaderFlowState.Uninitialized ||
            (FlowState, nextState) switch
            {
                (ReaderFlowState.Uninitialized, ReaderFlowState.Initialized) => true,
                (ReaderFlowState.Initialized, ReaderFlowState.WaitingForInitialAssets) => true,
                (ReaderFlowState.WaitingForInitialAssets, ReaderFlowState.Ready) => true,
                (ReaderFlowState.Ready, ReaderFlowState.WaitingForChapterAssets) => true,
                (ReaderFlowState.WaitingForChapterAssets, ReaderFlowState.Ready) => true,
                _ => false
            };

        if (!transitionAllowed)
        {
            throw new InvalidOperationException($"Invalid reader flow transition: {FlowState} -> {nextState}.");
        }

        FlowState = nextState;
    }

    private void TransitionPageCalculationTo(ReaderPageCalculationState nextState)
    {
        if (PageCalculationState == nextState) return;

        var transitionAllowed = nextState == ReaderPageCalculationState.NotStarted ||
            (PageCalculationState, nextState) switch
            {
                (ReaderPageCalculationState.NotStarted, ReaderPageCalculationState.CalculatingInitialPages) => true,
                (ReaderPageCalculationState.NotStarted, ReaderPageCalculationState.Ready) => true,
                (ReaderPageCalculationState.CalculatingInitialPages, ReaderPageCalculationState.NotStarted) => true,
                (ReaderPageCalculationState.Ready, ReaderPageCalculationState.RecalculatingPages) => true,
                (ReaderPageCalculationState.RecalculatingPages, ReaderPageCalculationState.Ready) => true,
                _ => false
            };

        if (!transitionAllowed)
        {
            throw new InvalidOperationException(
                $"Invalid page calculation transition: {PageCalculationState} -> {nextState}.");
        }

        PageCalculationState = nextState;
    }

    private void TransitionRecountTo(ReaderRecountState nextState)
    {
        if (_recountState == nextState) return;

        var transitionAllowed = nextState == ReaderRecountState.None ||
            (_recountState, nextState) switch
            {
                (ReaderRecountState.None, ReaderRecountState.Requested) => true,
                (ReaderRecountState.None, ReaderRecountState.RequestedDuringCalculation) => true,
                (ReaderRecountState.Requested, ReaderRecountState.RequestedDuringCalculation) => true,
                (ReaderRecountState.RequestedDuringCalculation, ReaderRecountState.Requested) => true,
                _ => false
            };

        if (!transitionAllowed)
        {
            throw new InvalidOperationException($"Invalid page recount transition: {_recountState} -> {nextState}.");
        }

        _recountState = nextState;
    }

    private static bool IsWaitingForAssets(ReaderFlowState state) =>
        state is ReaderFlowState.WaitingForInitialAssets or ReaderFlowState.WaitingForChapterAssets;

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

        ChangeChapter(State.ChapterNumber + 1, 1);
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

        ChangeChapter(State.ChapterNumber - 1, State.PagesPerChapter.ElementAtOrDefault(State.ChapterNumber - 1));
        OnTotalPagesChanged?.Invoke();
    }

    public void NextChapter()
    {
        if (State is null) return;
        if (State.ChapterNumber >= State.TotalChapters - 1) return;
        ChangeChapter(State.ChapterNumber + 1, 1);
    }

    public void PreviousChapter()
    {
        if (State is null) return;
        if (State.ChapterNumber <= 0) return;
        ChangeChapter(State.ChapterNumber - 1, 1);
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
        if (chapterChanged)
        {
            ChangeChapter(targetChapterIndex, 1);
        }
        else
        {
            State.SetChapterAndPage(targetChapterIndex, 1);
            MarkDirty();
            OnPageChanged?.Invoke();
        }
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
        TransitionFlowTo(ReaderFlowState.Uninitialized);
        TransitionPageCalculationTo(ReaderPageCalculationState.NotStarted);
        TransitionRecountTo(ReaderRecountState.None);
        State = null;
        _bookId = Guid.Empty;
        _profileId = Guid.Empty;
        _progress = null;
        _viewAdapter = null;
        _assetLoadToken = Guid.NewGuid();
        annotationService.Clear();
        // Waiting here also guarantees an in-flight save (whose state was already
        // captured locally) completes before the service is torn down.
        _saveTimer?.Dispose();
        _saveTimer = null;
        _calcCts?.Cancel();
        readerSettingsService.Dispose();
        GC.SuppressFinalize(this);
    }
}
