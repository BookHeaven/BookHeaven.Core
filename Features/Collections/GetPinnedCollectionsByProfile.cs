using BookHeaven.Core.Abstractions.Messaging;
using BookHeaven.Core.Entities.Base;
using BookHeaven.Core.Shared;

namespace BookHeaven.Core.Features.Collections;

public static class GetPinnedCollectionsByProfile
{
    public sealed record Query(Guid ProfileId) : IQuery<List<Collection>>;
    
    internal class Handler(IDbContextFactory<DatabaseContext> dbContextFactory) : IQueryHandler<Query, List<Collection>>
    {
        public async ValueTask<Result<List<Collection>>> Handle(Query request, CancellationToken cancellationToken)
        {
            await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

            return await dbContext.Collections.Where(c => (c.ProfileId == null || c.ProfileId == request.ProfileId) && c.Pinned == true).OrderBy(c => c.SortOrder).ToListAsync(cancellationToken);
        }
    }
}