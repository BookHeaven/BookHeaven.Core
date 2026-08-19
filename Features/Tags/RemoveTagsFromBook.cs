using BookHeaven.Core.Abstractions.Messaging;
using BookHeaven.Core.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BookHeaven.Core.Features.Tags;

public static class RemoveTagsFromBook
{
    public sealed record Command(IEnumerable<Guid> TagIds, Guid BookId) : ICommand;
    
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

            var tagsToRemove = book.Tags.Where(t => request.TagIds.Contains(t.TagId)).ToList();

            if (tagsToRemove.Count == 0)
            {
                return new Error("No matching tags found");
            }

            foreach (var tag in tagsToRemove)
            {
                book.Tags.Remove(tag);
                if (!context.Books.Any(b => b.Tags.Any(t => t.TagId == tag.TagId)))
                {
                    context.Tags.Remove(tag);
                }
            }

            try
            {
                await context.SaveChangesAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error removing tags from book");
                return new Error(ex.Message);
            }

            return Result.Success();
        }
    }
}