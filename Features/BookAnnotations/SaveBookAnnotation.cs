using BookHeaven.Core.Entities;
using BookHeaven.Core.Extensions;

namespace BookHeaven.Core.Features.BookAnnotations;

public static class SaveBookAnnotation
{
    public sealed record Command(BookAnnotation Annotation) : ICommand<Guid>;

    internal sealed class Handler(IDbContextFactory<DatabaseContext> dbContextFactory)
        : ICommandHandler<Command, Guid>
    {
        public async ValueTask<Result<Guid>> Handle(Command request, CancellationToken cancellationToken)
        {
            request.Annotation.NormalizeNote();
            await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var existing =
                await context.BookAnnotations.FirstOrDefaultAsync(
                    a => a.BookAnnotationId == request.Annotation.BookAnnotationId, cancellationToken);

            if (existing is null)
            {
                context.BookAnnotations.Add(request.Annotation);
            }
            else
            {
                existing.CopyContentFrom(request.Annotation);
            }
            try
            {
                await context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                return new Error("An error occurred while saving the book annotation");
            }

            return existing?.BookAnnotationId ?? request.Annotation.BookAnnotationId;
        }
    }
}
