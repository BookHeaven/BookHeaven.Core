using BookHeaven.Core.Abstractions.Messaging;
using BookHeaven.Core.Shared;
using Microsoft.Extensions.Logging;

namespace BookHeaven.Core.Features.BookSeries;

public static class DeleteSeries
{
    public sealed record Command(Guid SeriesId) : ICommand;
    
    internal class Handler(
        ILogger<Handler> logger,
        IDbContextFactory<DatabaseContext> dbContextFactory) : ICommandHandler<Command>
    {
        public async ValueTask<Result> Handle(Command request, CancellationToken cancellationToken)
        {
            await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var series = await dbContext.Series
                .Include(bs => bs.Books)
                .FirstOrDefaultAsync(bs => bs.SeriesId == request.SeriesId, cancellationToken);
            if (series is null)
            {
                return new Error("Series not found.");
            }
            
            if (series.Books.Count != 0)
            {
                foreach (var book in series.Books)
                {
                    book.SeriesIndex = null;
                }
            }

            try
            {
                dbContext.Remove(series);
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error deleting series with ID {SeriesId}", request.SeriesId);
                return new Error("Failed to delete series.");
            }
            
            return Result.Success();
        }
    }
}