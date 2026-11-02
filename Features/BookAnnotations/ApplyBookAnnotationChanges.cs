using BookHeaven.Core.Entities;
using BookHeaven.Core.Extensions;

namespace BookHeaven.Core.Features.BookAnnotations;

public static class ApplyBookAnnotationChanges
{
    public sealed record Command(
        Guid BookId,
        Guid ProfileId,
        List<BookAnnotation> Upserts,
        List<Guid> DeletedAnnotationIds) : ICommand;

    internal sealed class Handler(IDbContextFactory<DatabaseContext> dbContextFactory)
        : ICommandHandler<Command>
    {
        public async ValueTask<Result> Handle(Command request, CancellationToken cancellationToken)
        {
            if (request.Upserts.Any(annotation =>
                    annotation.BookId != request.BookId || annotation.ProfileId != request.ProfileId)
                || request.DeletedAnnotationIds.Any(annotationId => annotationId == Guid.Empty)
                || request.DeletedAnnotationIds.Distinct().Count() != request.DeletedAnnotationIds.Count
                || request.Upserts
                    .Where(annotation => annotation.BookAnnotationId != Guid.Empty)
                    .Select(annotation => annotation.BookAnnotationId)
                    .Intersect(request.DeletedAnnotationIds)
                    .Any())
            {
                return Result.Failure(new Error("Invalid book annotation changes"));
            }

            var affectedIds = request.Upserts
                .Where(annotation => annotation.BookAnnotationId != Guid.Empty)
                .Select(annotation => annotation.BookAnnotationId)
                .Concat(request.DeletedAnnotationIds)
                .Distinct()
                .ToList();

            await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var existingAnnotations = await context.BookAnnotations
                .Where(annotation => annotation.BookId == request.BookId
                                     && annotation.ProfileId == request.ProfileId
                                     && affectedIds.Contains(annotation.BookAnnotationId))
                .ToDictionaryAsync(annotation => annotation.BookAnnotationId, cancellationToken);

            foreach (var annotation in request.Upserts)
            {
                annotation.NormalizeNote();
                if (annotation.BookAnnotationId != Guid.Empty
                    && existingAnnotations.TryGetValue(annotation.BookAnnotationId, out var existing))
                {
                    existing.ChapterIndex = annotation.ChapterIndex;
                    existing.ChapterTitle = annotation.ChapterTitle;
                    existing.StartOffset = annotation.StartOffset;
                    existing.EndOffset = annotation.EndOffset;
                    existing.Text = annotation.Text;
                    existing.Note = annotation.Note;
                }
                else
                {
                    context.BookAnnotations.Add(annotation);
                }
            }

            foreach (var annotationId in request.DeletedAnnotationIds)
            {
                if (existingAnnotations.TryGetValue(annotationId, out var existing))
                {
                    context.BookAnnotations.Remove(existing);
                }
            }

            try
            {
                await context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                return Result.Failure(new Error("An error occurred while saving the book annotation changes"));
            }

            return Result.Success();
        }
    }
}
