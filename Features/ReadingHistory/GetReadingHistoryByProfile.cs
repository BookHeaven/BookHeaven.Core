using BookHeaven.Core.Entities;

namespace BookHeaven.Core.Features.ReadingHistory;

public static class GetReadingHistoryByProfile
{
    public sealed record Query(Guid ProfileId) : IQuery<List<ReadingHistoryEntry>>;

    internal class QueryHandler(IDbContextFactory<DatabaseContext> dbContextFactory) : IQueryHandler<Query, List<ReadingHistoryEntry>>
    {
        public async ValueTask<Result<List<ReadingHistoryEntry>>> Handle(Query request, CancellationToken cancellationToken)
        {
            await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);

            var entries = await context.ReadingHistory
                .AsNoTracking()
                .Where(rh => rh.ProfileId == request.ProfileId)
                .ToListAsync(cancellationToken);

            // SQLite can't order by DateTimeOffset in SQL
            return entries.OrderByDescending(rh => rh.DateFinished).ToList();
        }
    }
}