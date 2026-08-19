using BookHeaven.Core.Abstractions.Messaging;
using BookHeaven.Core.Entities;
using BookHeaven.Core.Shared;
using Microsoft.EntityFrameworkCore;

namespace BookHeaven.Core.Features.BooksProgress;

public static class CreateBookProgress
{
    public sealed record Command(Guid BookId, Guid ProfileId) : ICommand<Guid>;

    internal class Handler(IDbContextFactory<DatabaseContext> dbContextFactory) : ICommandHandler<Command, Guid>
    {
        public async ValueTask<Result<Guid>> Handle(Command request, CancellationToken cancellationToken)
        {
            await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);

            var progress = new BookProgress
            {
                BookId = request.BookId,
                ProfileId = request.ProfileId
            };

            try
            {
                await context.BooksProgress.AddAsync(progress, cancellationToken);
                await context.SaveChangesAsync(cancellationToken);
            }
            catch (Exception e)
            {
                return new Error(e.Message);
            }
            return progress.BookProgressId;
        }
    }
}

