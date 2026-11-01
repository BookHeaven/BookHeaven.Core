using BookHeaven.Core.Entities;

namespace BookHeaven.Core.Features.BookAnnotations;

public static class SyncBookAnnotations
{
    public sealed record Command(Guid BookId, Guid ProfileId, List<BookAnnotation> Annotations) : ICommand;

    internal sealed class Handler(IDbContextFactory<DatabaseContext> dbContextFactory)
        : ICommandHandler<Command>
    {
        public async ValueTask<Result> Handle(Command request, CancellationToken cancellationToken)
        {
            if (request.Annotations.Any(annotation =>
                    annotation.BookId != request.BookId || annotation.ProfileId != request.ProfileId))
            {
                return Result.Failure(new Error("Annotations must belong to the requested book and profile"));
            }

            await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var existingAnnotations = await context.BookAnnotations
                .Where(annotation => annotation.BookId == request.BookId && annotation.ProfileId == request.ProfileId)
                .ToListAsync(cancellationToken);
            var requestedById = request.Annotations
                .Where(annotation => annotation.BookAnnotationId != Guid.Empty)
                .ToDictionary(annotation => annotation.BookAnnotationId);

            foreach (var existing in existingAnnotations)
            {
                if (!requestedById.Remove(existing.BookAnnotationId, out var updated))
                {
                    context.BookAnnotations.Remove(existing);
                    continue;
                }

                existing.ChapterIndex = updated.ChapterIndex;
                existing.ChapterTitle = updated.ChapterTitle;
                existing.StartOffset = updated.StartOffset;
                existing.EndOffset = updated.EndOffset;
                existing.Text = updated.Text;
                existing.Note = BookAnnotation.NormalizeNote(updated.Note);
            }

            var newAnnotations = request.Annotations
                .Where(annotation => annotation.BookAnnotationId == Guid.Empty
                                     || requestedById.ContainsKey(annotation.BookAnnotationId))
                .ToList();
            var annotationsToAdd = newAnnotations.Select(annotation =>
                new BookAnnotation
                {
                    BookAnnotationId = annotation.BookAnnotationId,
                    BookId = annotation.BookId,
                    ProfileId = annotation.ProfileId,
                    ChapterIndex = annotation.ChapterIndex,
                    ChapterTitle = annotation.ChapterTitle,
                    StartOffset = annotation.StartOffset,
                    EndOffset = annotation.EndOffset,
                    Text = annotation.Text,
                    Note = BookAnnotation.NormalizeNote(annotation.Note)
                }).ToList();
            context.BookAnnotations.AddRange(annotationsToAdd);

            try
            {
                await context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                return Result.Failure(new Error("An error occurred while saving the book annotations"));
            }

            for (var index = 0; index < newAnnotations.Count; index++)
            {
                newAnnotations[index].BookAnnotationId = annotationsToAdd[index].BookAnnotationId;
            }

            return Result.Success();
        }
    }
}
