using BookHeaven.Core.Abstractions.Messaging;
using BookHeaven.Core.Shared;

namespace BookHeaven.Core.Features.KoreaderProgress;

public static class GetKoreaderProgress
{
    public sealed record Query(Guid ProfileId, string DocumentHash) : IQuery<Entities.KoreaderProgress>;
    
    internal class Handler(IDbContextFactory<DatabaseContext> dbContextFactory) : IQueryHandler<Query, Entities.KoreaderProgress>
    {
        public async ValueTask<Result<Entities.KoreaderProgress>> Handle(Query request, CancellationToken cancellationToken)
        {
            await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            
            var progress = await context.KoreaderProgress
                .FirstOrDefaultAsync(kp => 
                    kp.ProfileId == request.ProfileId && 
                    kp.DocumentHash == request.DocumentHash, 
                    cancellationToken);
            
            return progress != null ? progress : new Error("Progress not found");
        }
    }
}