using BookHeaven.Core.Abstractions.Messaging;
using BookHeaven.Core.Events;
using BookHeaven.Core.Services;
using BookHeaven.Core.Shared;

namespace BookHeaven.Core.Features.Collections;

public static class DeleteCollection
{
    public sealed record Command(Guid CollectionId) : ICommand;

    public sealed class Handler(
        IDbContextFactory<DatabaseContext> dbContextFactory,
        GlobalEventsService globalEventsService) : ICommandHandler<Command>
    {
        public async ValueTask<Result> Handle(Command request, CancellationToken cancellationToken)
        {
            await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var collection = await dbContext.Collections
                .FirstOrDefaultAsync(c => c.CollectionId == request.CollectionId, cancellationToken);
            if (collection is null)
            {
                return new Error("Collection not found");
            }

            dbContext.Collections.Remove(collection);

            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (Exception)
            {
                return new Error("Failed to delete collection");
            }

            await globalEventsService.Publish(new CollectionDeleted(request.CollectionId));

            return Result.Success();
        }
    }
}