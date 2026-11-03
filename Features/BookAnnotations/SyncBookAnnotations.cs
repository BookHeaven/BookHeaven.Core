using BookHeaven.Core.Entities;
using BookHeaven.Core.Extensions;

namespace BookHeaven.Core.Features.BookAnnotations;

public static class SyncBookAnnotations
{
    public sealed record Command(
        Guid BookId,
        Guid ProfileId,
        List<BookAnnotation> Annotations,
        List<Guid> DeletedAnnotationIds) : ICommand<List<BookAnnotation>>;

    /// <summary>
    /// Merges the server annotations into the local ones, returning the changes to apply locally.
    /// Last-write-wins by <c>UpdatedAt</c>; on a tie the server copy wins so both sides converge.
    /// </summary>
    public static (List<BookAnnotation> Upserts, List<Guid> DeletedAnnotationIds) Merge(
        List<BookAnnotation> localAnnotations,
        List<BookAnnotation> serverAnnotations)
    {
        var localIds = localAnnotations
            .Where(annotation => annotation.BookAnnotationId != Guid.Empty)
            .Select(annotation => annotation.BookAnnotationId)
            .ToHashSet();
        var serverById = serverAnnotations
            .Where(annotation => annotation.BookAnnotationId != Guid.Empty)
            .ToDictionary(annotation => annotation.BookAnnotationId);

        List<BookAnnotation> upserts = [];
        List<Guid> deletedAnnotationIds = [];

        foreach (var local in localAnnotations)
        {
            if (local.BookAnnotationId == Guid.Empty) continue;
            if (!serverById.TryGetValue(local.BookAnnotationId, out var server))
            {
                deletedAnnotationIds.Add(local.BookAnnotationId);
                continue;
            }

            if (local.HasContentChanged(server) && server.UpdatedAt >= local.UpdatedAt)
            {
                upserts.Add(server);
            }
        }

        foreach (var server in serverAnnotations)
        {
            if (server.BookAnnotationId != Guid.Empty && !localIds.Contains(server.BookAnnotationId))
            {
                upserts.Add(server);
            }
        }

        return (upserts, deletedAnnotationIds);
    }

    internal sealed class Handler(IDbContextFactory<DatabaseContext> dbContextFactory)
        : ICommandHandler<Command, List<BookAnnotation>>
    {
        public async ValueTask<Result<List<BookAnnotation>>> Handle(Command request, CancellationToken cancellationToken)
        {
            if (request.Annotations.Any(annotation =>
                    annotation.BookId != request.BookId || annotation.ProfileId != request.ProfileId)
                || request.DeletedAnnotationIds.Any(annotationId => annotationId == Guid.Empty)
                || request.DeletedAnnotationIds.Distinct().Count() != request.DeletedAnnotationIds.Count)
            {
                return new Error("Invalid book annotation sync request");
            }

            var deletedAnnotationIds = request.DeletedAnnotationIds.ToHashSet();
            await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var existingAnnotations = await context.BookAnnotations
                .Where(annotation => annotation.BookId == request.BookId && annotation.ProfileId == request.ProfileId)
                .ToListAsync(cancellationToken);
            var existingIds = existingAnnotations
                .Select(annotation => annotation.BookAnnotationId)
                .ToHashSet();
            var incomingById = request.Annotations
                .Where(annotation => annotation.BookAnnotationId != Guid.Empty)
                .ToDictionary(annotation => annotation.BookAnnotationId);

            foreach (var annotationId in request.DeletedAnnotationIds)
            {
                if (existingIds.Contains(annotationId))
                {
                    var existing = existingAnnotations.Single(annotation =>
                        annotation.BookAnnotationId == annotationId);
                    context.BookAnnotations.Remove(existing);
                }
            }

            foreach (var existing in existingAnnotations)
            {
                if (deletedAnnotationIds.Contains(existing.BookAnnotationId)) continue;
                if (incomingById.TryGetValue(existing.BookAnnotationId, out var incoming)
                    && existing.HasContentChanged(incoming)
                    && incoming.UpdatedAt > existing.UpdatedAt)
                {
                    existing.CopyContentFrom(incoming);
                    existing.Note = incoming.NormalizedNote();
                }
            }

            var annotationsToAdd = request.Annotations
                .Where(annotation => annotation.BookAnnotationId == Guid.Empty
                                     || (!existingIds.Contains(annotation.BookAnnotationId)
                                         && !deletedAnnotationIds.Contains(annotation.BookAnnotationId)))
                .Select(annotation => annotation.WithNote(annotation.NormalizedNote()))
                .ToList();
            context.BookAnnotations.AddRange(annotationsToAdd);

            try
            {
                await context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                return new Error("An error occurred while saving the book annotations");
            }

            // Return the merged state so the client can converge to it.
            return await context.BookAnnotations
                .AsNoTracking()
                .Where(annotation => annotation.BookId == request.BookId && annotation.ProfileId == request.ProfileId)
                .ToListAsync(cancellationToken);
        }
    }

}
