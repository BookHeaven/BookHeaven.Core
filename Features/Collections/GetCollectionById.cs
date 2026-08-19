using BookHeaven.Core.Abstractions.Messaging;
using BookHeaven.Core.Entities.Base;
using BookHeaven.Core.Shared;

namespace BookHeaven.Core.Features.Collections;

public static class GetCollectionById
{
    public sealed record Query(Guid CollectionId) : IQuery<Collection>;

    internal class Handler(IDbContextFactory<DatabaseContext> dbContextFactory) : IQueryHandler<Query, Collection>
    {
        public async ValueTask<Result<Collection>> Handle(Query request, CancellationToken cancellationToken)
        {
            await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

            var collection = await dbContext.Collections.FirstOrDefaultAsync(c => c.CollectionId == request.CollectionId, cancellationToken);

            if (collection is null)
            {
                return new Error("Collection not found");
            }
            
            return collection;
        }
    }
}