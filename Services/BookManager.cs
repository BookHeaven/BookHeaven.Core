using BookHeaven.Core.Abstractions;
using BookHeaven.Core.Entities;
using BookHeaven.Core.Entities.Base;
using BookHeaven.Core.Enums;
using BookHeaven.Core.Features.Books;
using BookHeaven.Core.Features.BooksProgress;
using BookHeaven.Core.Localization;
using BookHeaven.Core.Extensions;
using Mediator;

namespace BookHeaven.Core.Services;

public class BookManager(
    IAlertService alertService,
    ISender sender)
{
    private List<Book> _books = [];
    public List<Book> Books => (Filter == BookStatus.All ? _books : _books.Where(b => b.ReadingStatus() == Filter)).ApplyDefaultSorting().ToList();
    public bool IsEmpty => _books.Count == 0;
    public int CountByStatus(BookStatus status) => _books.GetCountByStatus(status);
    
    public BookStatus Filter { get; set; } = BookStatus.All;
    public Collection? CurrentCollection;
    
    public event Action? OnBooksChanged;

    /*private async Task ClearCache(Book book, bool showToast = true)
    {
        var progress = book.GetCachePath(CacheKey.Progress);
        var styles = book.GetCachePath(CacheKey.Styles);

        if (File.Exists(progress))
        {
            File.Delete(progress);
        }

        if (File.Exists(styles))
        {
            File.Delete(styles);
        }

        if (showToast) await alertService.ShowToast("Cache cleared");
    }*/
    
    public async Task GetBooksAsync(Guid profileId)
    {
        if (CurrentCollection is null)
        {
            var getBooks = await sender.Send(new GetAllBooks.Query(profileId));
            if (getBooks.IsSuccess)
            {
                _books = getBooks.Value;
            }
        }
        else
        {
            var getBooks = await sender.Send(new GetBooksByCollection.Query(CurrentCollection.CollectionId, profileId));
            if (getBooks.IsSuccess)
            {
                _books = getBooks.Value;
            }
        }
        
    }

    public async Task AppendBookAsync(Guid profileId, Guid bookId)
    {
        var getBook = await sender.Send(new GetBook.Query {BookId = bookId});
        if(getBook.IsFailure) return;
        
        var getProgress = await sender.Send(new GetBookProgressByProfile.Query(bookId,profileId));
        if(getProgress.IsFailure) return;
        
        var book = getBook.Value;
        book.Progresses.Add(getProgress.Value);
        
        if(CurrentCollection is not null && !book.BelongsToCollection(CurrentCollection)) return;
        _books.Add(book);
    }
    
    public void RemoveBook(Guid bookId)
    {
        var book = _books.FirstOrDefault(b => b.BookId == bookId);
        if (book != null)
        {
            _books.Remove(book);
            OnBooksChanged?.Invoke();
        }
    }
    
    public async Task MarkAsNewAsync(Guid bookId)
    {
        var book = _books.FirstOrDefault(b => b.BookId == bookId);
        if (book == null) return;
        
        var result = await alertService.ShowConfirmationAsync("Are you sure?", "This will reset your progress, which can't be undone unless you delete the book and download it again.");
        if (!result) return;
        
        var progress = book.Progress();
        progress.StartDate = null;
        progress.EndDate = null;
        progress.Progress = 0;
        progress.ElapsedTime = TimeSpan.Zero;
        progress.LastRead = null;
        progress.Chapter = 0;
        progress.ChapterProgress = 0;
        await sender.Send(new UpdateBookProgress.Command(progress));
        //await ClearCache(book, false);
        OnBooksChanged?.Invoke();
        await alertService.ShowToastAsync(Translations.BOOK_MARKED_AS_NEW);
    }
    
    public async Task MarkAsFinishedAsync(Guid bookId)
    {
        var book = _books.FirstOrDefault(b => b.BookId == bookId);
        if (book == null) return;
        
        var progress = book.Progress();
        if(progress.ElapsedTime == TimeSpan.Zero)
        {
            progress.StartDate = DateTimeOffset.Now;
        }
        progress.EndDate = DateTimeOffset.Now;
        progress.Progress = 100;
        await sender.Send(new UpdateBookProgress.Command(progress));
        //await ClearCache(book, false);
        OnBooksChanged?.Invoke();
        await alertService.ShowToastAsync(Translations.BOOK_MARKED_AS_FINISHED);
    }
}