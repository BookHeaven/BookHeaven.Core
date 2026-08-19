using BookHeaven.Core.Abstractions.Messaging;
using BookHeaven.Core.Shared;
using Microsoft.Extensions.Logging;

namespace BookHeaven.Core.Features.Authors;

public static class DeleteAuthor
{
    public sealed record Command(Guid AuthorId) : ICommand;
    
    internal class Handler(
        IDbContextFactory<DatabaseContext> dbContextFactory,
        ILogger<Handler> logger) : ICommandHandler<Command>
    {
        public async ValueTask<Result> Handle(Command command, CancellationToken cancellationToken = default)
        {
            await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var author = await dbContext.Authors
                .FirstOrDefaultAsync(a => a.AuthorId == command.AuthorId, cancellationToken);


            if (author is null)
            {
                return new Error("Author not found");
            }

            try
            {
                
                dbContext.Authors.Remove(author);
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (Exception e)
            {
                logger.LogError(e, "Failed to delete author");
                return new Error("An error occurred while deleting the author.");
            }
            return Result.Success();
        }
    }
}