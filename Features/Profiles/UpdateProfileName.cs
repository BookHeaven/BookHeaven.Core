using BookHeaven.Core.Abstractions.Messaging;
using BookHeaven.Core.Shared;
using Microsoft.EntityFrameworkCore;

namespace BookHeaven.Core.Features.Profiles;

public static class UpdateProfileName
{
    public sealed record Command(Guid Id, string Name) : ICommand;
    
    internal class Handler(IDbContextFactory<DatabaseContext> dbContextFactory) : ICommandHandler<Command>
    {
        public async ValueTask<Result> Handle(Command request, CancellationToken cancellationToken)
        {
            await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);

            var profile = await context.Profiles.FirstOrDefaultAsync(p => p.ProfileId == request.Id, cancellationToken: cancellationToken);
            if (profile == null)
            {
                return new Error("Profile not found.");
            }

            profile.Name = request.Name;

            try
            {
                await context.SaveChangesAsync(cancellationToken);
            }
            catch (Exception e)
            {
                return new Error(e.Message);
            }

            return Result.Success();
        }
    }
}