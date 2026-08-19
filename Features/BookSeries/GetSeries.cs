using BookHeaven.Core.Abstractions.Messaging;
using BookHeaven.Core.Entities;
using BookHeaven.Core.Shared;

namespace BookHeaven.Core.Features.BookSeries;

public static class GetSeries {
    public sealed record Query(Guid? SeriesId, string? Name = null): IQuery<Series>;

    internal class Handler(IDbContextFactory<DatabaseContext> dbContextFactory) : IQueryHandler<Query, Series>
    {
        public async ValueTask<Result<Series>> Handle(Query request, CancellationToken cancellationToken)
        {
            if(request.SeriesId == null && request.Name == null)
            {
                return new Error("You must provide either an SeriesId or a Name");
            }
        
            await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);

            try
            {
                var series = await context.Series.FirstOrDefaultAsync(x => 
                        (request.SeriesId != null && x.SeriesId == request.SeriesId) || 
                        (request.Name != null && x.Name!.ToUpper() == request.Name.ToUpper()),
                    cancellationToken);
            
                return series != null ? series : new Error("Series not found");
            }
            catch (Exception e)
            {
                return new Error(e.Message);
            }
        }
    }
}