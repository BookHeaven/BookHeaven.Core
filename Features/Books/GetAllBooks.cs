using BookHeaven.Core.Abstractions.Messaging;
using BookHeaven.Core.Entities;
using BookHeaven.Core.Shared;
using Microsoft.Extensions.Logging;

namespace BookHeaven.Core.Features.Books;

public static class GetAllBooks
{
    public sealed record Query(Guid? ProfileId = null, string? Filter = null) : IQuery<List<Book>>;

    internal class Handler(
        IDbContextFactory<DatabaseContext> dbContextFactory,
        ILogger<Handler> logger) : IQueryHandler<Query, List<Book>>
    {
        public async ValueTask<Result<List<Book>>> Handle(Query request, CancellationToken cancellationToken)
        {
            await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);

            try
            {
                var books = context.Books
                    .Include(b => b.Author)
                    .Include(b => b.Series)
                    .Include(b => b.Tags).AsQueryable();
            
                if(request.ProfileId is not null)
                {
                    books = books.Include(b => b.Progresses.Where(bp => bp.ProfileId == request.ProfileId));
                }
            
                if (!string.IsNullOrEmpty(request.Filter))
                {
                    books = books.Where(b =>
                        b.Title!.ToUpper().Contains(request.Filter.ToUpper()) ||
                        b.Author!.Name!.ToUpper().Contains(request.Filter.ToUpper()) ||
                        b.Series!.Name!.ToUpper().Contains(request.Filter.ToUpper()) ||
                        b.Tags.Any(t => t.Name.ToUpper().Contains(request.Filter.ToUpper())));
                }
            
                var results = await books.AsSplitQuery().ToListAsync(cancellationToken);

                return results;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to retrieve books");
                return new Error("Failed to retrieve books");
            }
            
        }
    }
}