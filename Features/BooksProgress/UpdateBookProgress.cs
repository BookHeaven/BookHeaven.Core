using BookHeaven.Core.Abstractions.Messaging;
using BookHeaven.Core.Entities;
using BookHeaven.Core.Shared;
using BookHeaven.Core.Extensions;

namespace BookHeaven.Core.Features.BooksProgress;

public static class UpdateBookProgress
{
    public sealed record Command(BookProgress BookProgress) : ICommand;

    internal class Handler(IDbContextFactory<DatabaseContext> dbContextFactory) : ICommandHandler<Command>
    {
        public async ValueTask<Result> Handle(Command request, CancellationToken cancellationToken)
        {
            await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);

            var progress = await context.BooksProgress.FirstOrDefaultAsync(bp => bp.BookProgressId == request.BookProgress.BookProgressId, cancellationToken);
            
            if (progress is null)
            {
                return Result.Failure(new Error("Book progress not found"));
            }
            
            var updated = request.BookProgress;
            var isFinished = progress is {StartDate: not null, EndDate: null} && updated is {StartDate: not null, EndDate: not null};
            if (isFinished)
            {
                var startDate = updated.StartDate!.Value;
                var endDate = updated.EndDate!.Value;
                var alreadyRecorded = await context.ReadingHistory.AnyAsync(rh =>
                    rh.ProfileId == updated.ProfileId &&
                    rh.BookId == updated.BookId &&
                    rh.DateStarted == startDate &&
                    rh.DateFinished == endDate, cancellationToken);

                if (!alreadyRecorded)
                {
                    await context.ReadingHistory.AddAsync(new ReadingHistoryEntry
                    {
                        BookId = updated.BookId,
                        ProfileId = updated.ProfileId,
                        DateStarted = startDate,
                        DateFinished = endDate,
                        ElapsedTime = updated.ElapsedTime
                    }, cancellationToken);
                }
            }
            
            progress.UpdateFrom(request.BookProgress);

            if (progress.Progress > 100)
            {
                progress.Progress = 100;
            }

            try
            {
                await context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                return Result.Failure(new Error("An error occurred while updating the book progress"));
            }

            return Result.Success();
        }
    }
}