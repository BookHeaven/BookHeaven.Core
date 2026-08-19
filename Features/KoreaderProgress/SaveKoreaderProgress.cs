using BookHeaven.Core.Abstractions.Messaging;
using BookHeaven.Core.Shared;

namespace BookHeaven.Core.Features.KoreaderProgress;

public static class SaveKoreaderProgress
{
    public sealed record Command(Entities.KoreaderProgress Progress) : ICommand;
    
    internal class Handler(IDbContextFactory<DatabaseContext> dbContextFactory) : ICommandHandler<Command>
    {
        public async ValueTask<Result> Handle(Command request, CancellationToken cancellationToken)
        {
            await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            
            var koProgress = await context.KoreaderProgress
                .FirstOrDefaultAsync(kp => 
                    kp.ProfileId == request.Progress.ProfileId && 
                    kp.DocumentHash == request.Progress.DocumentHash, 
                    cancellationToken);

            if (koProgress is null)
            {
                await context.AddAsync(request.Progress, cancellationToken);
            }
            else
            {
                koProgress.DeviceId = request.Progress.DeviceId;
                koProgress.DeviceName = request.Progress.DeviceName;
                koProgress.Percentage = request.Progress.Percentage;
                koProgress.Progress = request.Progress.Progress;
                koProgress.Timestamp = request.Progress.Timestamp;
            }

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