using BookHeaven.Core.Abstractions.Messaging;
using BookHeaven.Core.Entities;
using BookHeaven.Core.Shared;

namespace BookHeaven.Core.Features.Profiles;

public static class GetProfileByName
{
    public sealed record Query(string Name) : IQuery<Profile>;
    
    internal class Handler(IDbContextFactory<DatabaseContext> dbContextFactory) : IQueryHandler<Query, Profile>
    {
        public async ValueTask<Result<Profile>> Handle(Query request, CancellationToken cancellationToken)
        {
            await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var profile = await context.Profiles.FirstOrDefaultAsync(p => p.Name == request.Name, cancellationToken: cancellationToken);
            
            return profile != null ? profile : new Error("Profile not found");
        }
    }
}