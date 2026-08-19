using BookHeaven.Core.Abstractions.Messaging;
using BookHeaven.Core.Entities.Base;
using BookHeaven.Core.Events;
using BookHeaven.Core.Services;
using BookHeaven.Core.Shared;
using Microsoft.EntityFrameworkCore;

namespace BookHeaven.Core.Features.Collections;

public static class AddCollection
{
    public sealed record Command(Collection Collection) : ICommand<Guid>;
    
    internal class Handler(
        IDbContextFactory<DatabaseContext> dbContextFactory,
        GlobalEventsService eventsService) : ICommandHandler<Command, Guid>
    {
        public async ValueTask<Result<Guid>> Handle(Command request, CancellationToken cancellationToken)
        {
            await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            
            var sortOrder = await dbContext.Collections.MaxAsync(c => (int?)c.SortOrder, cancellationToken) ?? 0;
            request.Collection.SortOrder = sortOrder + 1;
            
            try
            {
                await dbContext.Collections.AddAsync(request.Collection, cancellationToken);
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (Exception)
            {
                return new Error("Error adding collection");
            }
            await eventsService.Publish(new CollectionCreated(request.Collection.CollectionId));
            return request.Collection.CollectionId;
        }
    }
}