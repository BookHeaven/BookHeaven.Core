namespace BookHeaven.Core.Features.BookAnnotations.Abstractions;

public interface IDeletedAnnotationHistory
{
    Task<List<Guid>> GetAsync(Guid bookId, Guid profileId, CancellationToken cancellationToken = default);

    Task QueueAsync(
        Guid bookId,
        Guid profileId,
        IReadOnlyCollection<Guid> annotationIds,
        CancellationToken cancellationToken = default);

    Task AcknowledgeAsync(
        Guid bookId,
        Guid profileId,
        IReadOnlyCollection<Guid> annotationIds,
        CancellationToken cancellationToken = default);

    Task ClearBookAsync(Guid bookId, CancellationToken cancellationToken = default);
}