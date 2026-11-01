using BookHeaven.Core.Entities;

namespace BookHeaven.Core.Features.BookAnnotations;

public static class DeleteBookAnnotation
{
    public sealed record Command(Guid BookId, Guid ProfileId, Guid BookAnnotationId) : ICommand;

    internal sealed class Handler(IDbContextFactory<DatabaseContext> dbContextFactory)
        : ICommandHandler<Command>
    {
        public async ValueTask<Result> Handle(Command request, CancellationToken cancellationToken)
        {
            await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var annotation = await context.BookAnnotations.FirstOrDefaultAsync(
                item => item.BookAnnotationId == request.BookAnnotationId
                        && item.BookId == request.BookId
                        && item.ProfileId == request.ProfileId,
                cancellationToken);
            if (annotation is null)
            {
                return Result.Failure(new Error("The book annotation could not be found"));
            }

            context.BookAnnotations.Remove(annotation);

            try
            {
                await context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                return Result.Failure(new Error("An error occurred while deleting the book annotation"));
            }

            return Result.Success();
        }
    }
}
