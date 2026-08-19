using BookHeaven.Core.Abstractions.Messaging;
using BookHeaven.Core.Entities;
using BookHeaven.Core.Shared;
using BookHeaven.Core.Extensions;

namespace BookHeaven.Core.Features.BookSeries;

public static class UpdateSeries {
    public sealed record Command(Series Series) : ICommand;

    internal class Handler(IDbContextFactory<DatabaseContext> dbContextFactory) : ICommandHandler<Command>
    {
        public async ValueTask<Result> Handle(Command request, CancellationToken cancellationToken)
        {
            await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            
            var existingSeries = await context.Series.FirstOrDefaultAsync(s => s.SeriesId == request.Series.SeriesId, cancellationToken);
            if (existingSeries is null)
            {
                return new Error("Series not found");
            }
            
            existingSeries.UpdateFrom(request.Series);

            try
            {
                await context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                return new Error("An error occurred while updating the series");
            }
            
            return Result.Success();
        }
    }
}