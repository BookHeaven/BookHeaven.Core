using BookHeaven.Core.Abstractions.Messaging;
using BookHeaven.Core.Entities;
using BookHeaven.Core.Shared;

namespace BookHeaven.Core.Features.Profiles;

public static class GetAllProfiles
{
    public sealed record Query(bool IncludeSettings = false) : IQuery<List<Profile>>;

    internal class Handler(IDbContextFactory<DatabaseContext> dbContextFactory)
        : IQueryHandler<Query, List<Profile>>
    {
        public async ValueTask<Result<List<Profile>>> Handle(Query request, CancellationToken cancellationToken)
        {
            await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            
            var query = context.Profiles.AsQueryable();
            if (request.IncludeSettings)
            {
                query = query.Include(p => p.ProfileSettings);
            }

            return await query.ToListAsync(cancellationToken);
        }
    }
}
