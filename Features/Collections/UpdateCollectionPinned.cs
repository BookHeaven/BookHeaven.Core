using BookHeaven.Core.Abstractions.Messaging;
using BookHeaven.Core.Services;
using BookHeaven.Core.Shared;

namespace BookHeaven.Core.Features.Collections;

public static class UpdateCollectionPinned
{
    public sealed record Command(Guid CollectionId, bool Pinned) : ICommand;

    internal sealed class Handler(
        IDbContextFactory<DatabaseContext> dbContextFactory,
        GlobalEventsService eventsService) : ICommandHandler<Command>
    {

        public async ValueTask<Result> Handle(Command request, CancellationToken cancellationToken)
        {
            await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

            var existingCollection = await dbContext.Collections.FirstOrDefaultAsync(c => c.CollectionId == request.CollectionId, cancellationToken);
            if (existingCollection == null)
            {
                return new Error("Collection not found.");
            }

            existingCollection.Pinned = request.Pinned;

            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                return new Error("An error occurred while updating the collection: " + ex.Message);
            }

            await eventsService.Publish(new Events.CollectionUpdated(request.CollectionId));

            return Result.Success();
        }
    }
}