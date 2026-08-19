using BookHeaven.Core.Abstractions.Messaging;
using BookHeaven.Core.Entities;
using BookHeaven.Core.Shared;
using Microsoft.Extensions.Logging;

namespace BookHeaven.Core.Features.Tags;

public static class AddTagsToBook
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="Names">Comma separated list of tags</param>
    /// <param name="BookId">Id of the book</param>
    public sealed record Command(string Names, Guid BookId) : ICommand<List<Tag>>;
    
    internal class CommandHandler(
        IDbContextFactory<DatabaseContext> dbContextFactory,
        ILogger<CommandHandler> logger) : ICommandHandler<Command, List<Tag>>
    {
        public async ValueTask<Result<List<Tag>>> Handle(Command request, CancellationToken cancellationToken)
        {
            await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var book = await context.Books.Include(b => b.Tags).FirstOrDefaultAsync(b => b.BookId == request.BookId, cancellationToken);

            if (book == null)
            {
                return new Error("Book not found");
            }

            List<Tag> tags = [];
            
            foreach (var tagName in request.Names.Split(",", StringSplitOptions.RemoveEmptyEntries))
            {
                var tag = await context.Tags.FirstOrDefaultAsync(t => t.Name == tagName, cancellationToken) ?? new()
                {
                    Name = tagName
                };

                if (book.Tags.Any(t => t.Name == tag.Name)) continue;
                
                book.Tags.Add(tag);
                tags.Add(tag);
            }
            
            try
            {
                await context.SaveChangesAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error adding tags to book");
                return new Error(ex.Message);
            }

            return tags;
        }
    }
}