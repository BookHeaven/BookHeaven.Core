using System.Text.Json;

namespace BookHeaven.Core.Features.BookAnnotations;

public interface IPendingBookAnnotationDeletionStore
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
}

public sealed class PendingBookAnnotationDeletionFileStore(string filePath) : IPendingBookAnnotationDeletionStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<List<Guid>> GetAsync(
        Guid bookId,
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var pendingDeletions = await LoadAsync(cancellationToken);
            return pendingDeletions
                .Where(deletion => deletion.BookId == bookId && deletion.ProfileId == profileId)
                .Select(deletion => deletion.AnnotationId)
                .ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task QueueAsync(
        Guid bookId,
        Guid profileId,
        IReadOnlyCollection<Guid> annotationIds,
        CancellationToken cancellationToken = default)
    {
        var uniqueAnnotationIds = ValidateIds(annotationIds);
        if (uniqueAnnotationIds.Count == 0) return;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var pendingDeletions = await LoadAsync(cancellationToken);
            foreach (var annotationId in uniqueAnnotationIds)
            {
                if (pendingDeletions.Any(deletion =>
                        deletion.BookId == bookId
                        && deletion.ProfileId == profileId
                        && deletion.AnnotationId == annotationId))
                {
                    continue;
                }

                pendingDeletions.Add(new PendingDeletion(bookId, profileId, annotationId));
            }

            await SaveAsync(pendingDeletions, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AcknowledgeAsync(
        Guid bookId,
        Guid profileId,
        IReadOnlyCollection<Guid> annotationIds,
        CancellationToken cancellationToken = default)
    {
        var annotationIdSet = ValidateIds(annotationIds).ToHashSet();
        if (annotationIdSet.Count == 0) return;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var pendingDeletions = await LoadAsync(cancellationToken);
            pendingDeletions.RemoveAll(deletion =>
                deletion.BookId == bookId
                && deletion.ProfileId == profileId
                && annotationIdSet.Contains(deletion.AnnotationId));
            await SaveAsync(pendingDeletions, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<List<PendingDeletion>> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath)) return [];

        var json = await File.ReadAllTextAsync(filePath, cancellationToken);
        return JsonSerializer.Deserialize<List<PendingDeletion>>(json)
               ?? throw new InvalidDataException("Pending annotation deletion file contains null data");
    }

    private async Task SaveAsync(List<PendingDeletion> pendingDeletions, CancellationToken cancellationToken)
    {
        var directoryPath = Path.GetDirectoryName(filePath)
                            ?? throw new InvalidOperationException("Pending annotation deletion path must have a directory");
        Directory.CreateDirectory(directoryPath);

        if (pendingDeletions.Count == 0)
        {
            if (File.Exists(filePath)) File.Delete(filePath);
            return;
        }

        var temporaryFilePath = $"{filePath}.tmp";
        var json = JsonSerializer.Serialize(pendingDeletions);
        await File.WriteAllTextAsync(temporaryFilePath, json, cancellationToken);
        File.Move(temporaryFilePath, filePath, true);
    }

    private static List<Guid> ValidateIds(IReadOnlyCollection<Guid> annotationIds)
    {
        ArgumentNullException.ThrowIfNull(annotationIds);
        var uniqueAnnotationIds = annotationIds.Distinct().ToList();
        if (uniqueAnnotationIds.Contains(Guid.Empty))
        {
            throw new ArgumentException("Annotation IDs must not be empty", nameof(annotationIds));
        }

        return uniqueAnnotationIds;
    }

    private sealed record PendingDeletion(Guid BookId, Guid ProfileId, Guid AnnotationId);
}
