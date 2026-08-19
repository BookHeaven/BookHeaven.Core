using BookHeaven.Core.Abstractions.Messaging;
using BookHeaven.Core.Entities;
using BookHeaven.Core.Shared;

namespace BookHeaven.Core.Features.Tags;

public static class GetAllTags
{
    public sealed record Query : IQuery<List<Tag>>;

    internal class Handler(IDbContextFactory<DatabaseContext> dbContextFactory) : IQueryHandler<Query, List<Tag>>
    {
        public async ValueTask<Result<List<Tag>>> Handle(Query request, CancellationToken cancellationToken)
        {
            await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

            return await dbContext.Tags.ToListAsync(cancellationToken);
        }
    }
}