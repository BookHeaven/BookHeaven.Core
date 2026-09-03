using BookHeaven.Core.Abstractions.Messaging;
using BookHeaven.Core.Events;
using BookHeaven.Core.Services;
using BookHeaven.Core.Shared;
using BookHeaven.Core.Extensions;
using BookHeaven.Core.Features.Reader.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BookHeaven.Core.Features.Books;

public static class DeleteBook
{
    public sealed record Command(Guid BookId) : ICommand;

    internal class CommandHandler(
        ILogger<CommandHandler> logger,
        IDbContextFactory<DatabaseContext> dbContextFactory,
        IReaderCacheService readerCacheService,
        GlobalEventsService globalEventsService) : ICommandHandler<Command>
    {
        public async ValueTask<Result> Handle(Command request, CancellationToken cancellationToken)
        {
            await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);

            var book = await context.Books
                .FirstOrDefaultAsync(b => b.BookId == request.BookId, cancellationToken);

            if (book == null)
            {
                return new Error("Book not found");
            }


            try
            {
                readerCacheService.ClearCache(request.BookId);
                if(File.Exists(book.EbookPath())) File.Delete(book.EbookPath());
                if(File.Exists(book.CoverPath())) File.Delete(book.CoverPath());
                context.Books.Remove(book);
                await context.SaveChangesAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to delete book with ID {BookId}", request.BookId);
                return new Error("Failed to delete book");
            }
           

            await globalEventsService.Publish(new BookDeleted(request.BookId));

            return Result.Success();
        }
    }
}