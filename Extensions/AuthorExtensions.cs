using BookHeaven.Core.Entities;

namespace BookHeaven.Core.Extensions;

public static class AuthorExtensions
{
    extension(Author author)
    {
        public void UpdateFrom(Author updatedAuthor)
        {
            author.Name = updatedAuthor.Name;
            author.Biography = updatedAuthor.Biography;
        }
    }
}