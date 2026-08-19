using BookHeaven.Core.Abstractions.Messaging;
using BookHeaven.Core.Entities;
using BookHeaven.Core.Shared;
using Microsoft.EntityFrameworkCore;

namespace BookHeaven.Core.Features.Profiles;

public static class CreateProfile
{
    public sealed record Response(Guid ProfileId, int TotalProfiles);
    
    public sealed record Command(string Name) : ICommand<Response>;

    internal class Handler(IDbContextFactory<DatabaseContext> dbContextFactory) : ICommandHandler<Command, Response>
    {

        public async ValueTask<Result<Response>> Handle(Command request, CancellationToken cancellationToken)
        {
            await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);

            var profile = new Profile
            {
                Name = request.Name
            };
            
            await context.Profiles.AddAsync(profile, cancellationToken);
            
            var books = await context.Books
                .AsNoTracking()
                .ToListAsync(cancellationToken);

            
            foreach (var book in books)
            {
                var progress = new BookProgress
                {
                    BookId = book.BookId,
                    ProfileId = profile.ProfileId,
                };
                await context.BooksProgress.AddAsync(progress, cancellationToken);
            }

            try 
            {
                await context.SaveChangesAsync(cancellationToken);
            }
            catch (Exception e)
            {
                return new Error(e.Message);
            }
        
            return new Response(profile.ProfileId, await context.Profiles.CountAsync(cancellationToken));
        }
    }
}