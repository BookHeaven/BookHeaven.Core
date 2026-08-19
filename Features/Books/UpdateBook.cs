using BookHeaven.Core.Abstractions.Messaging;
using BookHeaven.Core.Entities;
using BookHeaven.Core.Shared;
using BookHeaven.Core.Extensions;
using Microsoft.EntityFrameworkCore;

namespace BookHeaven.Core.Features.Books;

public static class UpdateBook
{
    public sealed record Command(Book Book, string? CoverSourcePath, string? EpubSourcePath) : ICommand;

    internal class Handler(IDbContextFactory<DatabaseContext> dbContextFactory) : ICommandHandler<Command>
    {
        public async ValueTask<Result> Handle(Command request, CancellationToken cancellationToken)
        {
            await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        
            var book = await context.Books
                .FirstOrDefaultAsync(b => b.BookId == request.Book.BookId, cancellationToken);
        
            if (book == null)
            {
                return new Error("Book not found");
            }
        
            book.UpdateFrom(request.Book);
        
            if(request.Book.Author is not null && !context.Authors.Any(a => a.AuthorId == request.Book.Author.AuthorId))
            {
                book.Author = request.Book.Author;
            }
            if(request.Book.Series is not null && !context.Series.Any(s => s.SeriesId == request.Book.Series.SeriesId))
            {
                book.Series = request.Book.Series;
            }

            try
            {
                await context.SaveChangesAsync(cancellationToken);
                await Utilities.StoreFile(request.CoverSourcePath, book.CoverPath(), cancellationToken);
                await Utilities.StoreFile(request.EpubSourcePath, book.EbookPath(), cancellationToken);
                
            }
            catch (DbUpdateException)
            {
                return new Error("An error occurred while updating the book");
            }

            return Result.Success();
        }
    }
}