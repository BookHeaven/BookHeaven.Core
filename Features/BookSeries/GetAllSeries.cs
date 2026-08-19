using BookHeaven.Core.Abstractions.Messaging;
using BookHeaven.Core.Entities;
using BookHeaven.Core.Shared;

namespace BookHeaven.Core.Features.BookSeries;

public static class GetAllSeries{
    public sealed record Query(bool IncludeBooks = false) : IQuery<List<Series>>;

    internal class Handler(IDbContextFactory<DatabaseContext> dbContextFactory) : IQueryHandler<Query, List<Series>>
    {
        public async ValueTask<Result<List<Series>>> Handle(Query request, CancellationToken cancellationToken)
        {
            await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        
            return request.IncludeBooks
                ? await context.Series.Include(x => x.Books).ThenInclude(b => b.Author).ToListAsync(cancellationToken)
                : await context.Series.ToListAsync(cancellationToken);
        }
    }
}