using BookHeaven.Core.Abstractions.Messaging;
using BookHeaven.Core.Entities;
using BookHeaven.Core.Events;
using BookHeaven.Core.Services;
using BookHeaven.Core.Shared;
using BookHeaven.Core.Extensions;

namespace BookHeaven.Core.Features.Books;

public static class AddBook
{
    public sealed record Command(Book Book, string CoverPath, string EpubPath, bool InitializeProgress = true) : ICommand<Guid>;

    internal class CommandHandler(
        IDbContextFactory<DatabaseContext> dbContextFactory,
        GlobalEventsService globalEventsService) : ICommandHandler<Command, Guid>
    {
        public async ValueTask<Result<Guid>> Handle(Command request, CancellationToken cancellationToken)
        {
            await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            
            await context.Books.AddAsync(request.Book, cancellationToken);
            
            if(request.Book.Author is not null && context.Authors.Any(a => a.AuthorId == request.Book.Author.AuthorId))
            {
                context.Entry(request.Book.Author).State = EntityState.Unchanged;
            }
            if(request.Book.Series is not null && context.Series.Any(s => s.SeriesId == request.Book.Series.SeriesId))
            {
                context.Entry(request.Book.Series).State = EntityState.Unchanged;
            }

            if (request.Book.Tags.Count > 0)
            {
                foreach (var tag in request.Book.Tags)
                {
                    var existingTag = await context.Tags.FirstOrDefaultAsync(t => t.TagId == tag.TagId, cancellationToken);
                    if (existingTag != null)
                    {
                        context.Entry(existingTag).State = EntityState.Unchanged;
                    }
                }
            }

            if (request.InitializeProgress)
            {
                var profiles = await context.Profiles.ToListAsync(cancellationToken);
                foreach (var profile in profiles)
                {
                    var existingProgress = await context.BooksProgress
                        .FirstOrDefaultAsync(bp => bp.BookId == request.Book.BookId && bp.ProfileId == profile.ProfileId, cancellationToken);
                    if (existingProgress is not null) continue;
                
                    var progress = new BookProgress
                    {
                        Book = request.Book,
                        Profile = profile
                    };
                    await context.BooksProgress.AddAsync(progress, cancellationToken);

                }
            }
            
            
            try 
            {
                await context.SaveChangesAsync(cancellationToken);
                await Utilities.StoreFile(request.CoverPath, request.Book.CoverPath(), cancellationToken);
                await Utilities.StoreFile(request.EpubPath, request.Book.EbookPath(), cancellationToken);
            }
            catch (Exception e)
            {
                return new Error(e.Message);
            }
            
            await globalEventsService.Publish(new BookAdded(request.Book.BookId));
            return request.Book.BookId;
        }
    }
}