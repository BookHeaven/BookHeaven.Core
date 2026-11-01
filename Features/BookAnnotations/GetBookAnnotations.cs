using BookHeaven.Core.Entities;

namespace BookHeaven.Core.Features.BookAnnotations;

public static class GetBookAnnotations
{
    public sealed record Query(Guid BookId, Guid ProfileId) : IQuery<List<BookAnnotation>>;

    internal sealed class Handler(IDbContextFactory<DatabaseContext> dbContextFactory)
        : IQueryHandler<Query, List<BookAnnotation>>
    {
        public async ValueTask<Result<List<BookAnnotation>>> Handle(Query request, CancellationToken cancellationToken)
        {
            await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var annotations = await context.BookAnnotations
                .AsNoTracking()
                .Where(annotation => annotation.BookId == request.BookId && annotation.ProfileId == request.ProfileId)
                .ToListAsync(cancellationToken);
            return annotations;
        }
    }
}
