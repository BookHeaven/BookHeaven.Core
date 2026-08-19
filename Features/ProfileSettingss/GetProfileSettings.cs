using BookHeaven.Core.Abstractions.Messaging;
using BookHeaven.Core.Entities;
using BookHeaven.Core.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BookHeaven.Core.Features.ProfileSettingss;

public static class GetProfileSettings
{
    
    public sealed record Query(Guid ProfileId) : IQuery<ProfileSettings>;

    internal class QueryHandler(
        IDbContextFactory<DatabaseContext> dbContextFactory,
        ILogger<QueryHandler> logger) : IQueryHandler<Query, ProfileSettings>
    {
        public async ValueTask<Result<ProfileSettings>> Handle(Query request, CancellationToken cancellationToken)
        {
            try
            {
                await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);
                var profileSettings = await context.ProfilesSettings
                    .FirstOrDefaultAsync(ps => ps.ProfileId == request.ProfileId, cancellationToken);
            
                return profileSettings != null
                    ? profileSettings
                    : new Error("Profile settings not found");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error getting profile settings for profile {ProfileId}", request.ProfileId);
                return new Error("Error getting profile settings");
            }
        }
    }
}