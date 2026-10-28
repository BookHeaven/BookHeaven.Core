using BookHeaven.Core.Entities;

namespace BookHeaven.Core.Helpers;

public static class BookRecommender
{
    private const double SeriesWeight = 3;
    private const double AuthorWeight = 1;

    public static List<Book> Recommend(
        IEnumerable<Book> allBooks,
        IEnumerable<BookProgress> localProgress,
        IEnumerable<BookProgress> remoteProgress,
        ISet<Guid>? excludedBookIds = null,
        int recentBooksCount = 5)
    {
        List<Book> books = allBooks.ToList();
        Dictionary<Guid, BookProgress> progressByBook = MergeProgress(localProgress, remoteProgress);

        List<Book> startedBooks = books
            .Where(book => excludedBookIds?.Contains(book.BookId) != true && IsStarted(progressByBook, book.BookId))
            .OrderByDescending(book => progressByBook[book.BookId].LastRead)
            .ThenBy(book => book.SeriesIndex)
            .ToList();

        List<Book> recentBooks = progressByBook.Values
            .Where(progress => progress.LastRead is not null)
            .OrderByDescending(progress => progress.LastRead)
            .Select(progress => books.FirstOrDefault(book => book.BookId == progress.BookId))
            .OfType<Book>()
            .Take(recentBooksCount)
            .ToList();

        if (recentBooks.Count == 0)
        {
            return startedBooks;
        }

        HashSet<Guid> startedBookIds = startedBooks.Select(book => book.BookId).ToHashSet();
        List<Book> scoredBooks = books
            .Where(book => !startedBookIds.Contains(book.BookId)
                           && !IsRead(progressByBook, book.BookId)
                           && excludedBookIds?.Contains(book.BookId) != true)
            .Select(book => (Book: book, Score: Score(book, recentBooks)))
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Book.Author?.Name)
            .ThenBy(candidate => candidate.Book.Series?.Name)
            .ThenBy(candidate => candidate.Book.SeriesIndex)
            .Select(candidate => candidate.Book)
            .ToList();

        return [.. startedBooks, .. scoredBooks];
    }

    private static bool IsStarted(Dictionary<Guid, BookProgress> progressByBook, Guid bookId)
    {
        return progressByBook.TryGetValue(bookId, out BookProgress? progress)
               && !IsRead(progressByBook, bookId)
               && (progress.Progress > 0 || progress.ElapsedTime != TimeSpan.Zero);
    }

    private static Dictionary<Guid, BookProgress> MergeProgress(IEnumerable<BookProgress> localProgress, IEnumerable<BookProgress> remoteProgress)
    {
        Dictionary<Guid, BookProgress> merged = [];
        foreach (BookProgress progress in remoteProgress)
        {
            merged[progress.BookId] = progress;
        }
        foreach (BookProgress progress in localProgress)
        {
            if (HasActivity(progress) || !merged.ContainsKey(progress.BookId))
            {
                merged[progress.BookId] = progress;
            }
        }
        return merged;
    }

    private static bool HasActivity(BookProgress progress)
    {
        return progress.EndDate is not null
               || progress.LastRead is not null
               || progress.Progress > 0
               || progress.ElapsedTime != TimeSpan.Zero;
    }

    private static bool IsRead(Dictionary<Guid, BookProgress> progressByBook, Guid bookId)
    {
        return progressByBook.TryGetValue(bookId, out BookProgress? progress)
               && (progress.EndDate is not null || progress.Progress >= 100);
    }

    private static double Score(Book candidate, List<Book> recentBooks)
    {
        double score = 0;
        for (int rank = 0; rank < recentBooks.Count; rank++)
        {
            Book recent = recentBooks[rank];
            if (recent.BookId == candidate.BookId)
            {
                continue;
            }

            double recencyWeight = 1.0 / (rank + 1);
            if (candidate.SeriesId is not null && candidate.SeriesId == recent.SeriesId)
            {
                score += SeriesWeight * recencyWeight;
            }
            if (candidate.AuthorId is not null && candidate.AuthorId == recent.AuthorId)
            {
                score += AuthorWeight * recencyWeight;
            }
        }
        return score;
    }
}
