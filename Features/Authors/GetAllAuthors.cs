using BookHeaven.Core.Abstractions.Messaging;
using BookHeaven.Core.Entities;
using BookHeaven.Core.Shared;

namespace BookHeaven.Core.Features.Authors;

public static class GetAllAuthors
{
    public sealed record Query(bool IncludeBooks = false) : IQuery<List<Author>>;

    internal class Handler(IDbContextFactory<DatabaseContext> dbContextFactory)
        : IQueryHandler<Query, List<Author>>
    {
        public async ValueTask<Result<List<Author>>> Handle(Query request, CancellationToken cancellationToken)
        {
            await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);

            return request.IncludeBooks
                ? await context.Authors.Include(x => x.Books).ThenInclude(b => b.Series).ToListAsync(cancellationToken)
                : await context.Authors.ToListAsync(cancellationToken);
        }
    }
}
