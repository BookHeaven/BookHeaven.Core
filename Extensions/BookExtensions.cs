using BookHeaven.Core.Entities;
using BookHeaven.Core.Entities.Base;
using BookHeaven.Core.Enums;
using BookHeaven.Core.Helpers;

namespace BookHeaven.Core.Extensions;

public static class BookExtensions
{
	extension(Book book)
    {
	    public string FormattedFileName() => $"{(book.Author != null).Then($"{book.Author?.Name} - ")}{(book.Series != null).Then($"{book.Series?.Name} ({book.SeriesIndex?.ToString("0.##")}) - ")}{book.Title}{book.Format.GetExtension()}";
	    public BookProgress Progress() => book.Progresses.First();

	    public BookStatus ReadingStatus()
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

	    public string EbookUrl() => "/books/" + book.BookId + book.Format.GetExtension();

		public string CoverUrl(bool appendVersion = false)
		{
			var coverExists = File.Exists(book.CoverPath());
			if (!coverExists) return "/img/no-cover.jpg";

			var url = "/covers/" + book.BookId + ".jpg";
			if(!appendVersion) return url;

			return url + "?v=" + new FileInfo(book.CoverPath()).LastWriteTimeUtc.Ticks;
		}

	    public string EbookPath() => Path.Combine(CoreGlobals.BooksPath, $"{book.BookId}{book.Format.GetExtension()}");
	    public string CoverPath() => Path.Combine(CoreGlobals.CoversPath, $"{book.BookId}.jpg");

	    public string GetCoverAsBase64()
	    {
		    var path = book.CoverPath();
		    if (!File.Exists(path))
		    {
			    return string.Empty;
		    }
		    return "data:image/jpeg;base64," + Convert.ToBase64String(File.ReadAllBytes(path));
	    }
	    
	    public bool BelongsToCollection(Collection collection)
	    {
		    return collection switch
		    {
			    SimpleCollection simpleCollection => simpleCollection.BookIds.Contains(book.BookId),
			    SmartCollection smartCollection => new[] { book }.ApplyCollectionFilter(smartCollection).Any(),
			    _ => throw new NotImplementedException($"Collection type {collection.GetType().Name} not implemented")
		    };
	    }

	    public void UpdateFrom(Book updatedBook)
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
		    book.FileHash = updatedBook.FileHash;
	    }
    }

    extension(IEnumerable<Book> books)
    {
	    public IEnumerable<Book> ApplyDefaultSorting()
	    {
		    return books.OrderBy(b => b.Author?.Name).ThenBy(b => b.Series?.Name).ThenBy(b => b.SeriesIndex);
	    }

	    public IEnumerable<Book> ApplyCollectionFilter(Collection collection)
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

	    public int GetCountByStatus(BookStatus status) => books.Count(b => b.ReadingStatus() == status);
    }
}