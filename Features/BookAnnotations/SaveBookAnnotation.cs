using BookHeaven.Core.Entities;

namespace BookHeaven.Core.Features.BookAnnotations;

public static class SaveBookAnnotation
{
    public sealed record Command(BookAnnotation Annotation) : ICommand<Guid>;

    internal sealed class Handler(IDbContextFactory<DatabaseContext> dbContextFactory)
        : ICommandHandler<Command, Guid>
    {
        public async ValueTask<Result<Guid>> Handle(Command request, CancellationToken cancellationToken)
        {
            request.Annotation.Note = BookAnnotation.NormalizeNote(request.Annotation.Note);
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
                existing.ChapterIndex = request.Annotation.ChapterIndex;
                existing.ChapterTitle = request.Annotation.ChapterTitle;
                existing.StartOffset = request.Annotation.StartOffset;
                existing.EndOffset = request.Annotation.EndOffset;
                existing.Text = request.Annotation.Text;
                existing.Note = request.Annotation.Note;
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
