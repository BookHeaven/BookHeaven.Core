using BookHeaven.Domain.Entities;
using BookHeaven.Domain.Extensions;

namespace BookHeaven.Domain.Features.BookSeries;

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