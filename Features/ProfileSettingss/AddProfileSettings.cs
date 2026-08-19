using BookHeaven.Core.Abstractions.Messaging;
using BookHeaven.Core.Entities;
using BookHeaven.Core.Shared;
using Microsoft.EntityFrameworkCore;

namespace BookHeaven.Core.Features.ProfileSettingss;

public static class AddProfileSettings
{
    public sealed record Command(ProfileSettings ProfileSettings) : ICommand;

    internal class Handler(IDbContextFactory<DatabaseContext> dbContextFactory) : ICommandHandler<Command>
    {
        public async ValueTask<Result> Handle(Command request, CancellationToken cancellationToken)
        {
            await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);

            await context.ProfilesSettings.AddAsync(request.ProfileSettings, cancellationToken);
            await context.SaveChangesAsync(cancellationToken);

            return Result.Success();
        }
    }
}