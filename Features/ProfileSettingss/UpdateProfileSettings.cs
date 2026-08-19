using BookHeaven.Core.Abstractions.Messaging;
using BookHeaven.Core.Entities;
using BookHeaven.Core.Shared;
using BookHeaven.Core.Extensions;

namespace BookHeaven.Core.Features.ProfileSettingss;

public static class UpdateProfileSettings
{
    public sealed record Command(ProfileSettings ProfileSettings) : ICommand;
    
    internal class CommandHandler(IDbContextFactory<DatabaseContext> dbContextFactory) : ICommandHandler<Command>
    {
        public async ValueTask<Result> Handle(Command request, CancellationToken cancellationToken)
        {
            await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            
            var existingProfileSettings = await context.ProfilesSettings
                .FirstOrDefaultAsync(ps => ps.ProfileId == request.ProfileSettings.ProfileId, cancellationToken);
            
            if (existingProfileSettings is null)
            {
                return new Error("Profile settings not found");
            }

            existingProfileSettings.UpdateFrom(request.ProfileSettings);

            try
            {
                await context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                return new Error("An error occurred while updating the profile settings");
            }

            return Result.Success();
        }
    }
    
}