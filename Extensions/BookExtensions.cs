using BookHeaven.Domain.Entities;
using BookHeaven.Domain.Entities.Base;
using BookHeaven.Domain.Enums;
using BookHeaven.Domain.Helpers;

namespace BookHeaven.Domain.Extensions;

public static class BookExtensions
{
	public static string FormattedFileName(this Book book) => $"{(book.Author != null).Then($"{book.Author?.Name} - ")}{(book.Series != null).Then($"{book.Series?.Name} ({book.SeriesIndex?.ToString("0.##")}) - ")}{book.Title}{book.Format.GetExtension()}";
    public static BookProgress Progress(this Book book) => book.Progresses.First();
    public static BookStatus ReadingStatus(this Book book)
    {
        if (book.Progress().EndDate != null)
        {
            return BookStatus.Finished;
        }
        
        if (book.Progress().ElapsedTime != TimeSpan.Zero || book.Progress().Progress > 0)
        {
            return BookStatus.Reading;
        }

        return BookStatus.New;
    }
    
    public static int GetCountByStatus(this List<Book> books, BookStatus status) => books.Count(b => b.ReadingStatus() == status);
    
    [Obsolete("Use EbookUrl instead")]
    public static string EpubUrl(this Book book) => "/books/" + book.BookId + ".epub";
    public static string EbookUrl(this Book book) => "/books/" + book.BookId + book.Format.GetExtension();

    public static string CoverUrl(this Book book, bool appendVersion = false)
    {
	    var coverExists = File.Exists(book.CoverPath());
	    if (!coverExists) return "/img/no-cover.jpg";
	    
	    var url = "/covers/" + book.BookId + ".jpg";
	    if(!appendVersion) return url;
	    
	    return url + "?v=" + new FileInfo(book.CoverPath()).LastWriteTimeUtc.Ticks;
    }
    
    [Obsolete("Use EbookPath instead")]
    public static string EpubPath(this Book book) => Path.Combine(DomainGlobals.BooksPath, $"{book.BookId}.epub");
    public static string EbookPath(this Book book) => Path.Combine(DomainGlobals.BooksPath, $"{book.BookId}{book.Format.GetExtension()}");
    public static string CoverPath(this Book book) => Path.Combine(DomainGlobals.CoversPath, $"{book.BookId}.jpg");

    public static string GetCoverAsBase64(this Book book)
	{
		var path = book.CoverPath();
		if (!File.Exists(path))
		{
			return string.Empty;
		}
		return "data:image/jpeg;base64," + Convert.ToBase64String(File.ReadAllBytes(path));
	}

	public static void UpdateFrom(this Book book, Book updatedBook)
	{
		book.Title = updatedBook.Title;
		book.AuthorId = updatedBook.AuthorId;
		book.SeriesId = updatedBook.SeriesId;
		book.SeriesIndex = updatedBook.SeriesIndex;
		book.Publisher = updatedBook.Publisher;
		book.PublishedDate = updatedBook.PublishedDate;
		book.Description = updatedBook.Description;
		book.ISBN10 = updatedBook.ISBN10;
		book.ISBN13 = updatedBook.ISBN13;
		book.ASIN = updatedBook.ASIN;
		book.UUID = updatedBook.UUID;
		book.Language = updatedBook.Language;
	}
	
	public static IEnumerable<Book> ApplyDefaultSorting(this IEnumerable<Book> books)
	{
		return books.OrderBy(b => b.Author?.Name).ThenBy(b => b.Series?.Name).ThenBy(b => b.SeriesIndex);
	}

	public static IEnumerable<Book> ApplyCollectionFilter(this IEnumerable<Book> books, Collection collection)
	{
		switch (collection)
		{
			case SimpleCollection simpleCollection:
				return books.Where(b => simpleCollection.BookIds.Contains(b.BookId)).ToList();
			case SmartCollection smartCollection:
				// Include logic: OR between all includes, if all are empty include all
				var hasAnyInclude = smartCollection.Authors.Include.Count > 0 ||
									 smartCollection.Series.Include.Count > 0 ||
									 smartCollection.Statuses.Include.Count > 0 ||
									 smartCollection.Tags.Include.Count > 0;

				var filtered = books.Where(b =>
					!hasAnyInclude || (
						(smartCollection.Authors.Include.Count > 0 && smartCollection.Authors.Include.Contains(b.AuthorId ?? Guid.Empty)) ||
						(smartCollection.Series.Include.Count > 0 && smartCollection.Series.Include.Contains(b.SeriesId ?? Guid.Empty)) ||
						(smartCollection.Statuses.Include.Count > 0 && smartCollection.Statuses.Include.Contains(b.ReadingStatus())) ||
						(smartCollection.Tags.Include.Count > 0 && b.Tags.Any(t => smartCollection.Tags.Include.Contains(t.TagId)))
					)
				);

				// Exclude logic: OR between all excludes
				filtered = filtered.Where(b =>
					(smartCollection.Authors.Exclude.Count == 0 || !smartCollection.Authors.Exclude.Contains(b.AuthorId ?? Guid.Empty)) &&
					(smartCollection.Series.Exclude.Count == 0 || !smartCollection.Series.Exclude.Contains(b.SeriesId ?? Guid.Empty)) &&
					(smartCollection.Statuses.Exclude.Count == 0 || !smartCollection.Statuses.Exclude.Contains(b.ReadingStatus())) &&
					(smartCollection.Tags.Exclude.Count == 0 || !b.Tags.Any(t => smartCollection.Tags.Exclude.Contains(t.TagId)))
				);

				return filtered;
			default:
				throw new NotImplementedException($"Collection type {collection.GetType().Name} not implemented");
		}
	}

	
}