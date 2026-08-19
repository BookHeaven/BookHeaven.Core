using BookHeaven.Core.Abstractions.Messaging;
using BookHeaven.Core.Entities;
using BookHeaven.Core.Shared;
using Microsoft.EntityFrameworkCore;

namespace BookHeaven.Core.Features.Books;

public static class GetBook
{
    public sealed record Query : IQuery<Book>
    {
        public Guid? BookId { get; init; }
        public string? Title { get; init; }
        public string? Hash { get; init; }
        
        public bool IsEmpty => BookId is null && string.IsNullOrEmpty(Title) && string.IsNullOrEmpty(Hash);
    }

    internal class Handler(IDbContextFactory<DatabaseContext> dbContextFactory) : IQueryHandler<Query, Book>
    {
        public async ValueTask<Result<Book>> Handle(Query request, CancellationToken cancellationToken)
        {
            if(request.IsEmpty)
            {
                return new Error("You must provide a filter");
            }

            await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);

            var book = await context.Books
                .Include(b => b.Author)
                .Include(b => b.Series)
                .Include(b => b.Tags)
                .AsSplitQuery()
                .FirstOrDefaultAsync(x => 
                    (request.BookId != null && x.BookId == request.BookId) || 
                    (!string.IsNullOrEmpty(request.Title) && x.Title == request.Title) ||
                    (!string.IsNullOrEmpty(request.Hash) && x.FileHash == request.Hash)
                    , cancellationToken);

            return book != null ? book : new Error("Book not found");
        }
    }
}
