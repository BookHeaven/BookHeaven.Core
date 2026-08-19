using BookHeaven.Core.Abstractions.Messaging;
using BookHeaven.Core.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BookHeaven.Core.Features.Tags;

public static class RemoveTagFromBook
{
    public sealed record Command(Guid TagId, Guid BookId) : ICommand;
    
    internal class CommandHandler(
        IDbContextFactory<DatabaseContext> dbContextFactory,
        ILogger<CommandHandler> logger) : ICommandHandler<Command>
    {
        public async ValueTask<Result> Handle(Command request, CancellationToken cancellationToken)
        {
            await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            
            var book = await context.Books
                .Include(b => b.Tags)
                .FirstOrDefaultAsync(b => b.BookId == request.BookId, cancellationToken);

            if (book == null)
            {
                return new Error("Book not found");
            }

            var tag = book.Tags.FirstOrDefault(t => t.TagId == request.TagId);

            if (tag == null)
            {
                return new Error("Tag not found");
            }

            book.Tags.Remove(tag);

            if (!context.Books.Any(b => b.Tags.Any(t => t.TagId == request.TagId)))
            {
                context.Tags.Remove(tag);
            }

            try
            {
                await context.SaveChangesAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error removing tag from book");
                return new Error(ex.Message);
            }

            return Result.Success();
        }
    }
}