using BookHeaven.Core.Abstractions.Messaging;
using BookHeaven.Core.Events;
using BookHeaven.Core.Services;
using BookHeaven.Core.Shared;

namespace BookHeaven.Core.Features.Collections;

public static class UpdateCollectionsPosition
{
    public sealed record Command(List<(Guid, int)> CollectionPositions) : ICommand;
    
    public sealed class Handler(
        IDbContextFactory<DatabaseContext> dbContextFactory,
        GlobalEventsService globalEventsService) : ICommandHandler<Command>
    {
        public async ValueTask<Result> Handle(Command request, CancellationToken cancellationToken)
        {
            await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var collections = await dbContext.Collections
                .Where(c => request.CollectionPositions.Select(cp => cp.Item1).Contains(c.CollectionId))
                .ToListAsync(cancellationToken);
            
            foreach (var (collectionId, position) in request.CollectionPositions)
            {
                var collection = collections.FirstOrDefault(c => c.CollectionId == collectionId);
                if (collection is null) continue;
                collection.SortOrder = position;
            }

            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (Exception)
            {
                return new Error("Failed to update collection positions");
            }

            await globalEventsService.Publish(new CollectionsPositionsUpdated(request.CollectionPositions));
            
            return Result.Success();
        }
    }
}