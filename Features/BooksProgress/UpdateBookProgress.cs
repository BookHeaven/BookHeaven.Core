using BookHeaven.Domain.Entities;
using BookHeaven.Domain.Extensions;

namespace BookHeaven.Domain.Features.BooksProgress;

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