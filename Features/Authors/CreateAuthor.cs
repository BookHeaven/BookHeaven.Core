using BookHeaven.Core.Abstractions.Messaging;
using BookHeaven.Core.Entities;
using BookHeaven.Core.Shared;
using Microsoft.EntityFrameworkCore;

namespace BookHeaven.Core.Features.Authors;

public static class CreateAuthor
{
    public sealed record Command(string Name) : ICommand<Author>;

    internal class Handler(IDbContextFactory<DatabaseContext> dbContextFactory) : ICommandHandler<Command, Author>
    {
        public async ValueTask<Result<Author>> Handle(Command request, CancellationToken cancellationToken)
        {
            await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);

            Author author = new()
            {
                Name = request.Name
            };
        
            await context.Authors.AddAsync(author, cancellationToken);
        
            try 
            {
                await context.SaveChangesAsync(cancellationToken);
            }
            catch (Exception e)
            {
                return new Error(e.Message);
            }

            return author;
        }
    }
}

