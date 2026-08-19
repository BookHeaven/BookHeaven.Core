using BookHeaven.Domain.Entities;

namespace BookHeaven.Domain.Extensions;

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