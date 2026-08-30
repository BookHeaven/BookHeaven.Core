using BookHeaven.Core.Entities;
using BookHeaven.Core.Extensions;
using BookHeaven.Core.Features.Books;
using BookHeaven.Core.Features.BooksProgress;
using BookHeaven.Core.Features.Reader.Abstractions;
using BookHeaven.Core.Features.Reader.Models;
using BookHeaven.EbookManager;
using BookHeaven.EbookManager.Abstractions;
using BookHeaven.EbookManager.Entities;
using BookHeaven.EbookManager.Enums;
using Mediator;

namespace BookHeaven.Core.Features.Reader.Services;

public class ReaderService(
    IReaderSettingsService readerSettingsService,
    EbookManagerProvider ebookManagerProvider,
    ISender sender) : IReaderService
{
    public bool IsReady { get; private set; }
    
    public ReaderState? State { get; private set; }
    public ProfileSettings? Settings => readerSettingsService.ReaderSettings;

    public event Action? OnPageChanged;
    public event Action? OnChapterChanged;
    public event Action? OnTotalPagesChanged;

    private IEbookReader? _reader;
    private BookProgress? _progress;
    

    /*private DateTimeOffset _suspendStartTime;
    private TimeSpan _totalSuspendedTime;*/
    
    public async Task<Result> InitializeAsync(Guid bookId, Guid profileId)
    {
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
        
        _reader = ebookManagerProvider.GetReader((Format)book.Format);
        State.Ebook = await _reader.ReadAllAsync(book.EbookPath());
        if (State.Styles.Count == 0)
        {
            State.Styles = State.Ebook.Content.Stylesheets;
        }
        NavigateTo(0, _progress?.Chapter ?? 0);
        State.EntryTime = DateTimeOffset.UtcNow;
        return Result.Success();
    }
    
    public async Task LoadCurrentContentAsync()
    {
        if(State is null) return;
        var tasks = new List<Task>();
                
        if (State.CurrentChapter is { IsContentProcessed: false })
        {
            tasks.Add(Task.Run(async () =>
            {
                State.CurrentChapter.Content = await _reader!.ApplyHtmlProcessingAsync(State.CurrentChapter.Content);
                State.CurrentChapter.IsContentProcessed = true;
            }));
        }
        if (State.PreviousChapter is { IsContentProcessed: false })
        {
            tasks.Add(Task.Run(async () =>
            {
                State.PreviousChapter.Content = await _reader!.ApplyHtmlProcessingAsync(State.PreviousChapter.Content);
                State.PreviousChapter.IsContentProcessed = true;
            }));
        }
        if (State.NextChapter is { IsContentProcessed: false })
        {
            tasks.Add(Task.Run(async () =>
            {
                State.NextChapter.Content = await _reader!.ApplyHtmlProcessingAsync(State.NextChapter.Content);
                State.NextChapter.IsContentProcessed = true;
            }));
        }
        if (tasks.Count > 0) await Task.WhenAll(tasks);
    }
    
    public void SetTotalPages(int totalPages, int? totalPagesPrev = null, int? totalPagesNext = null)
    {
        if(State is null) return;
        State.TotalPagesPrev = totalPagesPrev ?? 0;
        State.TotalPagesNext = totalPagesNext ?? 0;
        State.TotalPages = totalPages;
        if (State.PageNumber > totalPages)
        {
            State.PageNumber = totalPages;
        }
        OnTotalPagesChanged?.Invoke();
    }
    
    public void NavigateTo(int page, int chapter)
    {
        if(State is null) return;
        State.ChapterNumber = chapter;
        State.PageNumber = page;

        
        OnChapterChanged?.Invoke();
    }
    
    public void NavigateToInitialPage()
    {
        if(State is null) return;
        var targetPage = 1;

        if (_progress?.ChapterProgress > 0)
        {
            targetPage = (int)Math.Round(_progress.ChapterProgress * State.TotalPages);
        }
        targetPage = Math.Clamp(targetPage, 1, State.TotalPages);
        
        _progress?.StartDate ??= DateTimeOffset.Now;
        NavigateTo(targetPage, _progress?.Chapter ?? 0);
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
        if (State is null) return;
        if (State.PageNumber < State.TotalPages)
        {
            State.PageNumber++;
            OnPageChanged?.Invoke();
            return;
        }
        
        if(State.ChapterNumber == State.TotalChapters - 1) return;
            
        State.TotalPagesPrev = State.TotalPages;
        State.TotalPages = State.TotalPagesNext;
        State.TotalPagesNext = 0;
            
        State.PageNumber = 1;
        State.ChapterNumber++;
        OnChapterChanged?.Invoke();
        OnTotalPagesChanged?.Invoke();
    }
    
    public void PreviousPage()
    {
        if (State is null) return;
        if (State.PageNumber > 1)
        {
            State.PageNumber--;
            OnPageChanged?.Invoke();
            return;
        }
        
        if(State.ChapterNumber == 0) return;
            
        State.TotalPagesNext = State.TotalPages;
        State.TotalPages = State.TotalPagesPrev;
        State.TotalPagesPrev = 0;
            
        State.PageNumber = State.TotalPages;
        State.ChapterNumber--;
        OnChapterChanged?.Invoke();
        OnTotalPagesChanged?.Invoke();
    }

    public void NavigateToNextChapter()
    {
        if (State is null) return;
        if (State.ChapterNumber >= State.TotalChapters - 1) return;
        State.ChapterNumber++;
        State.PageNumber = 1;
        OnChapterChanged?.Invoke();
    }
    
    public void NavigateToPreviousChapter()
    {
        if (State is null) return;
        if (State.ChapterNumber <= 0) return;
        State.ChapterNumber--;
        State.PageNumber = 1;
        OnChapterChanged?.Invoke();
    }
    
    public async Task SaveProgressAsync()
    {
        if (State is null || _progress is null || State.TotalPages == 0) return;
        
        _progress.Chapter = State.ChapterNumber;
        _progress.ChapterProgress = State.PageNumber / (double)State.TotalPages;
        _progress.BookWordCount = State.TotalWeight;

        if (_progress.EndDate is null)
        {
            _progress.Progress = State.ProgressAsPercentage;
            _progress.ElapsedTime += DateTimeOffset.UtcNow - State.EntryTime;
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
        _progress = null;
        _reader?.Dispose();
        readerSettingsService.Dispose();
        GC.SuppressFinalize(this);
    }
}