using BookHeaven.Core.Entities;
using BookHeaven.Core.Entities.Base;
using BookHeaven.Core.Enums;

namespace BookHeaven.Core.Features.Books;

public static class GetBooksByCollection
{
    public sealed record Query(Guid CollectionId, Guid ProfileId) : IQuery<List<Book>>;
    
    internal class Handler(IDbContextFactory<DatabaseContext> dbContextFactory) : IQueryHandler<Query, List<Book>>
    {
        public async ValueTask<Result<List<Book>>> Handle(Query request, CancellationToken cancellationToken)
        {
            await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

            var collection = await dbContext.Collections.FirstOrDefaultAsync(c => c.CollectionId == request.CollectionId, cancellationToken);

            if (collection is null)
            {
                return new Error("Collection not found");
            }

            var books = await dbContext.Books
                .Include(b => b.Author)
                .Include(b => b.Series)
                .Include(b => b.Tags)
                .Include(b => b.Progresses.Where(p => p.ProfileId == request.ProfileId))
                .ApplyCollectionFilter(collection)
                .AsSplitQuery()
                .ToListAsync(cancellationToken);
            

            return books;
        }
    }
    
    private static IQueryable<Book> ApplyCollectionFilter(this IQueryable<Book> books, Collection collection)
    {
        return collection switch
        {
            SimpleCollection simpleCollection =>
                books.Where(b => simpleCollection.BookIds.Contains(b.BookId)),

            SmartCollection smartCollection =>
                ApplySmartCollectionFilter(books, smartCollection),

            _ => throw new NotImplementedException($"Collection type {collection.GetType().Name} not implemented")
        };
    }

    private static IQueryable<Book> ApplySmartCollectionFilter(IQueryable<Book> books, SmartCollection smartCollection)
    {
        var hasAnyInclude = smartCollection.Authors.Include.Count > 0 ||
                            smartCollection.Series.Include.Count > 0 ||
                            smartCollection.Statuses.Include.Count > 0 ||
                            smartCollection.Tags.Include.Count > 0;

        var filtered = books.Where(b =>
            !hasAnyInclude || (
                (smartCollection.Authors.Include.Count > 0 && smartCollection.Authors.Include.Contains(b.AuthorId ?? Guid.Empty)) ||
                (smartCollection.Series.Include.Count > 0 && smartCollection.Series.Include.Contains(b.SeriesId ?? Guid.Empty)) ||
                (smartCollection.Tags.Include.Count > 0 && b.Tags.Any(t => smartCollection.Tags.Include.Contains(t.TagId))) ||
                (smartCollection.Statuses.Include.Count > 0 && b.Progresses.Any(p =>
                    (p.EndDate != null && smartCollection.Statuses.Include.Contains(BookStatus.Finished)) ||
                    (p.EndDate == null && (p.Progress > 0 || p.ElapsedTime != TimeSpan.Zero) &&
                     smartCollection.Statuses.Include.Contains(BookStatus.Reading)) ||
                    (p.EndDate == null && p.Progress == 0 && p.ElapsedTime == TimeSpan.Zero &&
                     smartCollection.Statuses.Include.Contains(BookStatus.New))
                ))
            )
        );

        filtered = filtered.Where(b =>
            (smartCollection.Authors.Exclude.Count == 0 || !smartCollection.Authors.Exclude.Contains(b.AuthorId ?? Guid.Empty)) &&
            (smartCollection.Series.Exclude.Count == 0 || !smartCollection.Series.Exclude.Contains(b.SeriesId ?? Guid.Empty)) &&
            (smartCollection.Tags.Exclude.Count == 0 || !b.Tags.Any(t => smartCollection.Tags.Exclude.Contains(t.TagId))) &&
            (smartCollection.Statuses.Exclude.Count == 0 || !b.Progresses.Any(p =>
                (p.EndDate != null && smartCollection.Statuses.Exclude.Contains(BookStatus.Finished)) ||
                (p.EndDate == null && (p.Progress > 0 || p.ElapsedTime != TimeSpan.Zero) &&
                 smartCollection.Statuses.Exclude.Contains(BookStatus.Reading)) ||
                (p.EndDate == null && p.Progress == 0 && p.ElapsedTime == TimeSpan.Zero &&
                 smartCollection.Statuses.Exclude.Contains(BookStatus.New))
            ))
        );

        return filtered;
    }
}