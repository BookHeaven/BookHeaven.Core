using BookHeaven.Core.Abstractions.Messaging;
using BookHeaven.Core.Entities;
using BookHeaven.Core.Shared;
using Microsoft.EntityFrameworkCore;

namespace BookHeaven.Core.Features.BooksProgress;

public static class GetAllBooksProgressByProfile
{
    public sealed record Query(Guid ProfileId) : IQuery<List<BookProgress>>;

    internal class Handler(IDbContextFactory<DatabaseContext> dbContextFactory) : IQueryHandler<Query, List<BookProgress>>
    {
        public async ValueTask<Result<List<BookProgress>>> Handle(Query request, CancellationToken cancellationToken)
        {
            await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);

            var progress = await context.BooksProgress.Where(p => p.ProfileId == request.ProfileId)
                .ToListAsync(cancellationToken);

            return progress.Count > 0 ? progress : new Error("Progress not found");
        }
    }
}