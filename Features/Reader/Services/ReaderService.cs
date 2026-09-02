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
        State.SetEbook(await _reader.ReadAllAsync(book.EbookPath()));
        NavigateTo(1, 0);
        State.EntryTime = DateTimeOffset.UtcNow;
        return Result.Success();
    }
    
    public void PauseTimer()
    {
        State?.SuspendStartTime = DateTimeOffset.UtcNow;
    }
    
    public void ResumeTimer()
    {
        State?.TotalSuspendedTime += DateTimeOffset.UtcNow - State.SuspendStartTime;
    }

    public void SetTotalPages(int[] pageArray)
    {
        State?.SetPagesPerChapter(pageArray);
        OnTotalPagesChanged?.Invoke();
    }

    public void NavigateTo(int page, int chapter)
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

        if (_progress?.ChapterProgress > 0)
        {
            targetPage = (int)Math.Round(_progress.ChapterProgress * State.TotalPages);
        }

        targetChapter = Math.Clamp(targetChapter, 0, State.TotalChapters - 1);
        var chapterPageCount = State.PagesPerChapter.ElementAtOrDefault(targetChapter);
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
        if (State is null) return;
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
        if (State is null) return;
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
        _progress.ChapterProgress = State.PageNumber / (double)State.TotalPages;

        if (_progress.EndDate is null)
        {
            _progress.Progress = State.ProgressAsPercentage;
            _progress.ElapsedTime += DateTimeOffset.UtcNow - State.EntryTime - State.TotalSuspendedTime;
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