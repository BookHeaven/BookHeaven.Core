using BookHeaven.Core.Abstractions.Messaging;
using BookHeaven.Core.Entities;
using BookHeaven.Core.Shared;
using BookHeaven.Core.Extensions;

namespace BookHeaven.Core.Features.Authors;

public static class UpdateAuthor
{
    public sealed record Command(Author Author) : ICommand;

    internal class Handler(IDbContextFactory<DatabaseContext> dbContextFactory) : ICommandHandler<Command>
    {
        public async ValueTask<Result> Handle(Command request, CancellationToken cancellationToken)
        {
            await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            
            var existingAuthor = await context.Authors.FirstOrDefaultAsync(a => a.AuthorId == request.Author.AuthorId, cancellationToken);
            if (existingAuthor == null)
            {
                return new Error("Author not found");
            }
            
            existingAuthor.UpdateFrom(request.Author);
        
            try
            {
                await context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                return new Error("An error occurred while updating the author");
            }
        
            return Result.Success();
        }
    }
}