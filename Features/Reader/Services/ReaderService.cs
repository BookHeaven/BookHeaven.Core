using BookHeaven.Core.Abstractions.Services;
using BookHeaven.Core.DOM.Services.Abstractions;
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
    ISender sender) : IReaderService
{
    public bool IsReady { get; private set; }
    public bool IsCalculatingPages { get; private set; }
    
    public ReaderState? State { get; private set; }
    public ProfileSettings? Settings => readerSettingsService.ReaderSettings;

    public event Action? OnPageChanged;
    public event Action? OnChapterChanged;
    public event Action? OnTotalPagesChanged;

    private Guid _bookId;
    private BookProgress? _progress;

    // Tracks the in-flight page calculation so a new call can cancel the previous
    // one. Each calculation owns its CTS and disposes it in its own finally block;
    // this field is only ever null or a live (non-disposed) source.
    private CancellationTokenSource? _calcCts;
    
    public async Task<Result> InitializeAsync(Guid bookId, Guid profileId)
    {
        _bookId = bookId;
        State = new();
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
        
        
        State.SetEbook(await LoadEbookAsync(book));
        
        
        var cachedPages = await readerCacheService.LoadCachedPagesAsync(_bookId, Settings!.CalculateHash());
        if (cachedPages.Length > 0)
        {
            State.SetPagesPerChapter(cachedPages);
            NavigateToInitialPage();
            IsReady = true;
        }
        State.StartTimer();
        return Result.Success();
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
        reader.Dispose();
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

    public void SetTotalPages(int[] pageArray)
    {
        if (State is null) return;
        State.SetPagesPerChapter(pageArray);
        OnTotalPagesChanged?.Invoke();
        IsReady = true;
        _ = readerCacheService.SaveCachedPagesAsync(_bookId, Settings!.CalculateHash(), State.PagesPerChapter);
    }
    
    public async Task CalculatePagesAsync(int pageWidthPx, int pageHeightPx)
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

        var options = Settings.ToPageCalculatorOptions(pageWidthPx, pageHeightPx);
        try
        {
            var pageCounts = await pageCalculator.CalculatePagesAsync(State.Ebook, options, cts.Token);
            State.SetPagesPerChapter(pageCounts);
            OnTotalPagesChanged?.Invoke();
            _ = readerCacheService.SaveCachedPagesAsync(_bookId, Settings.CalculateHash(), State.PagesPerChapter);
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

    private void NavigateTo(int page, int chapter)
    {
        if(State is null) return;
        State.SetChapterAndPage(chapter, page);
        OnChapterChanged?.Invoke();
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
            OnPageChanged?.Invoke();
            return;
        }
        
        if(State.ChapterNumber == State.TotalChapters - 1) return;
            
        State.SetChapterAndPage(State.ChapterNumber + 1, 1);
        OnChapterChanged?.Invoke();
        OnTotalPagesChanged?.Invoke();
    }
    
    public void PreviousPage()
    {
        if (State is null || State.TotalPages <= 0) return;
        if (State.PageNumber > 1)
        {
            State.SetChapterAndPage(State.ChapterNumber, State.PageNumber - 1);
            OnPageChanged?.Invoke();
            return;
        }
        
        if(State.ChapterNumber == 0) return;
            
        State.SetChapterAndPage(State.ChapterNumber - 1, State.PagesPerChapter.ElementAtOrDefault(State.ChapterNumber - 1));
        OnChapterChanged?.Invoke();
        OnTotalPagesChanged?.Invoke();
    }
    
    public void NextChapter()
    {
        if (State is null) return;
        if (State.ChapterNumber >= State.TotalChapters - 1) return;
        State.SetChapterAndPage(State.ChapterNumber + 1, 1);
        OnChapterChanged?.Invoke();
    }
    
    public void PreviousChapter()
    {
        if (State is null) return;
        if (State.ChapterNumber <= 0) return;
        State.SetChapterAndPage(State.ChapterNumber - 1, 1);
        OnChapterChanged?.Invoke();
    }
    
    public async Task SaveProgressAsync()
    {
        if (State is null || _progress is null || State.TotalPages == 0) return;
        
        _progress.Chapter = State.ChapterNumber;
        _progress.ChapterProgress = (double)State.ChapterProgress;

        if (_progress.EndDate is null)
        {
            _progress.Progress = State.ProgressAsPercentage;
            _progress.ElapsedTime += State.GetElapsedReadingTime();
            _progress.LastRead = DateTimeOffset.Now;

            if (State.ChapterNumber == State.Ebook!.Content.Chapters.Count - 1 &&
                State.PageNumber == State.TotalPages)
            {
                _progress.EndDate = DateTimeOffset.Now;
            }
        }
        
        await sender.Send(new UpdateBookProgress.Command(_progress));
    }
    
    public void Dispose()
    {
        IsReady = false;
        State = null;
        _bookId = Guid.Empty;
        _progress = null;
        // Stop any in-flight calculation. Only cancel here — the task's finally
        // block owns the disposal, so we never double-dispose the CTS.
        _calcCts?.Cancel();
        readerSettingsService.Dispose();
        GC.SuppressFinalize(this);
    }
}