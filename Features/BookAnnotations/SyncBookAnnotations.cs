using BookHeaven.Core.Entities;
using BookHeaven.Core.Extensions;

namespace BookHeaven.Core.Features.BookAnnotations;

public static class SyncBookAnnotations
{
    public sealed record Command(Guid BookId, Guid ProfileId, List<BookAnnotation> Annotations) : ICommand<List<BookAnnotation>>;

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
                    annotation.BookId != request.BookId || annotation.ProfileId != request.ProfileId))
            {
                return new Error("Annotations must belong to the requested book and profile");
            }

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

            // Last-write-wins: an incoming annotation only replaces the stored one when its
            // content differs and it is newer. Stored annotations missing from the incoming
            // list are kept, so changes made on the server survive the sync.
            foreach (var existing in existingAnnotations)
            {
                if (incomingById.TryGetValue(existing.BookAnnotationId, out var incoming)
                    && existing.HasContentChanged(incoming)
                    && incoming.UpdatedAt > existing.UpdatedAt)
                {
                    existing.ChapterIndex = incoming.ChapterIndex;
                    existing.ChapterTitle = incoming.ChapterTitle;
                    existing.StartOffset = incoming.StartOffset;
                    existing.EndOffset = incoming.EndOffset;
                    existing.Text = incoming.Text;
                    existing.Note = incoming.NormalizedNote();
                }
            }

            var annotationsToAdd = request.Annotations
                .Where(annotation => annotation.BookAnnotationId == Guid.Empty
                                     || !existingIds.Contains(annotation.BookAnnotationId))
                .Select(annotation => new BookAnnotation
                {
                    BookAnnotationId = annotation.BookAnnotationId,
                    BookId = annotation.BookId,
                    ProfileId = annotation.ProfileId,
                    ChapterIndex = annotation.ChapterIndex,
                    ChapterTitle = annotation.ChapterTitle,
                    StartOffset = annotation.StartOffset,
                    EndOffset = annotation.EndOffset,
                    Text = annotation.Text,
                    Note = annotation.NormalizedNote()
                }).ToList();
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
