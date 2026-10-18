using BookHeaven.Core.Abstractions.Messaging;
using BookHeaven.Core.Features.Reader.Abstractions;
using BookHeaven.Core.Shared;
using Microsoft.EntityFrameworkCore;

namespace BookHeaven.Core.Features.Profiles;

public static class DeleteProfile
{
    public sealed record Command(Guid Id) : ICommand;
    
    internal class Handler(
        IDbContextFactory<DatabaseContext> dbContextFactory,
        IReaderCacheService readerCacheService) : ICommandHandler<Command>
    {
        public async ValueTask<Result> Handle(Command request, CancellationToken cancellationToken)
        {
            await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);

            var profile = await context.Profiles.FirstOrDefaultAsync(p => p.ProfileId == request.Id, cancellationToken: cancellationToken);
            if (profile == null)
            {
                return new Error("Profile not found.");
            }

            context.Profiles.Remove(profile);

            try
            {
                await context.SaveChangesAsync(cancellationToken);
            }
            catch (Exception e)
            {
                return new Error(e.Message);
            }

            // Drop the profile's page-count caches so no orphaned files remain
            await readerCacheService.RemoveProfileAsync(request.Id);

            return Result.Success();
        }
    }
}