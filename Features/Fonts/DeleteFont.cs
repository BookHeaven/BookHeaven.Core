using BookHeaven.Core.Abstractions.Messaging;
using BookHeaven.Core.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BookHeaven.Core.Features.Fonts;

public static class DeleteFont
{
    public sealed record Command(string FamilyName) : ICommand;
    
    internal class Handler(
        IDbContextFactory<DatabaseContext> dbContextFactory,
        ILogger<Handler> logger) : ICommandHandler<Command>
    {
        public async ValueTask<Result> Handle(Command request, CancellationToken cancellationToken)
        {
            await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);

            var variants = await context.Fonts.Where(v => v.Family == request.FamilyName).ToListAsync(cancellationToken: cancellationToken);

            try
            {
                
                context.Fonts.RemoveRange(variants);

                await context.SaveChangesAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, ex.Message);
                return new Error("Error deleting font");
            }

            return Result.Success();
        }
    }
}