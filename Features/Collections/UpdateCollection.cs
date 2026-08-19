using BookHeaven.Core.Abstractions.Messaging;
using BookHeaven.Core.Entities;
using BookHeaven.Core.Entities.Base;
using BookHeaven.Core.Services;
using BookHeaven.Core.Shared;

namespace BookHeaven.Core.Features.Collections;

public static class UpdateCollection
{
    public sealed record Command(Collection Collection) : ICommand;
    
    internal sealed class Handler(
        IDbContextFactory<DatabaseContext> dbContextFactory,
        GlobalEventsService eventsService) : ICommandHandler<Command>
    {

        public async ValueTask<Result> Handle(Command request, CancellationToken cancellationToken)
        {
            await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            
            var existingCollection = await dbContext.Collections.FirstOrDefaultAsync(c => c.CollectionId == request.Collection.CollectionId, cancellationToken);
            if (existingCollection == null)
            {
                return new Error("Collection not found.");
            }
            
            existingCollection.Name = request.Collection.Name;
            existingCollection.Pinned = request.Collection.Pinned;
            existingCollection.ProfileId = request.Collection.ProfileId;
            if (request.Collection is SimpleCollection simpleCollection)
            {
                var existingSimpleCollection = existingCollection as SimpleCollection;
                existingSimpleCollection!.BookIds = simpleCollection.BookIds;
            }
            else if(request.Collection is SmartCollection smartCollection)
            {
                var existingSmartCollection = existingCollection as SmartCollection;
                existingSmartCollection!.Authors = smartCollection.Authors;
                existingSmartCollection.Series = smartCollection.Series;
                existingSmartCollection.Statuses = smartCollection.Statuses;
                existingSmartCollection.Tags = smartCollection.Tags;
            }

            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                return new Error("An error occurred while updating the collection: " + ex.Message);
            }
            
            await eventsService.Publish(new Events.CollectionUpdated(request.Collection.CollectionId));
            
            return Result.Success();
        }
    }
}