using BookHeaven.Core.Entities;

namespace BookHeaven.Core.Features.ReadingHistory;

public static class SyncReadingHistory
{
    public sealed record Command(List<ReadingHistoryEntry> Entries) : ICommand;

    internal class CommandHandler(IDbContextFactory<DatabaseContext> dbContextFactory) : ICommandHandler<Command>
    {
        public async ValueTask<Result> Handle(Command request, CancellationToken cancellationToken)
        {
            if (request.Entries.Count == 0) return Result.Success();

            await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);

            var profileIds = request.Entries.Select(entry => entry.ProfileId).Distinct().ToList();
            var bookIds = request.Entries.Select(entry => entry.BookId).Distinct().ToList();

            var existingProfileIds = (await context.Profiles
                .Where(profile => profileIds.Contains(profile.ProfileId))
                .Select(profile => profile.ProfileId)
                .ToListAsync(cancellationToken)).ToHashSet();
            var existingBookIds = (await context.Books
                .Where(book => bookIds.Contains(book.BookId))
                .Select(book => book.BookId)
                .ToListAsync(cancellationToken)).ToHashSet();

            var knownEntries = await context.ReadingHistory
                .AsNoTracking()
                .Where(entry => profileIds.Contains(entry.ProfileId) && bookIds.Contains(entry.BookId))
                .Select(entry => new { entry.ProfileId, entry.BookId, entry.DateStarted, entry.DateFinished })
                .ToListAsync(cancellationToken);
            var knownKeys = knownEntries
                .Select(entry => (entry.ProfileId, entry.BookId, entry.DateStarted, entry.DateFinished))
                .ToHashSet();

            foreach (var entry in request.Entries)
            {
                var isValid = existingProfileIds.Contains(entry.ProfileId) && existingBookIds.Contains(entry.BookId);
                var isNew = knownKeys.Add((entry.ProfileId, entry.BookId, entry.DateStarted, entry.DateFinished));
                if (!isValid || !isNew) continue;

                await context.ReadingHistory.AddAsync(new ReadingHistoryEntry
                {
                    ReadingHistoryEntryId = entry.ReadingHistoryEntryId,
                    ProfileId = entry.ProfileId,
                    BookId = entry.BookId,
                    DateStarted = entry.DateStarted,
                    DateFinished = entry.DateFinished,
                    ElapsedTime = entry.ElapsedTime
                }, cancellationToken);
            }

            try
            {
                await context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                return Result.Failure(new Error("An error occurred while syncing the reading history"));
            }

            return Result.Success();
        }
    }
}
