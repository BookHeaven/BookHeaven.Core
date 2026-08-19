using BookHeaven.Core.Abstractions.Messaging;
using BookHeaven.Core.Entities;
using BookHeaven.Core.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BookHeaven.Core.Features.Fonts;

public static class AddFont
{
    public sealed record Command(Font Font) : ICommand<Font>;
    
    internal class CommandHandler(
        IDbContextFactory<DatabaseContext> dbContextFactory,
        ILogger<CommandHandler> logger) : ICommandHandler<Command, Font>
    {
        public async ValueTask<Result<Font>> Handle(Command request, CancellationToken cancellationToken)
        {
            await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            
            var font = new Font
            {
                Family = request.Font.Family,
                Style = request.Font.Style,
                Weight = request.Font.Weight,
                FileName = request.Font.FileName
            };

            context.Fonts.Add(font);

            try
            {
                await context.SaveChangesAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error adding font");
                return new Error(ex.Message);
            }

            return font;
        }
    }
}