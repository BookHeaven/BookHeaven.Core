using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;
using BookHeaven.Core.Entities.Base;
using BookHeaven.Core.Extensions;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BookHeaven.Core.Entities;

public partial class Author : BaseEntity
{
    public Guid AuthorId { get; set; }
    public string? Name { get; set; }
    public string? Biography { get; set; }
    public string? ImageUrl { get; set; }
    [JsonIgnore]
    public List<Book> Books { get; set; } = [];
}

internal class AuthorConfig : BaseEntityConfig<Author>
{
    public override void Configure(EntityTypeBuilder<Author> builder)
    {
        base.Configure(builder);
        builder.HasKey(a => a.AuthorId);
        builder.Property(a => a.AuthorId).ValueGeneratedOnAdd();
        
        builder.Property(a => a.Name).HasMaxLength(150);
        builder.Property(a => a.Biography).HasMaxLength(500);
        builder.Property(a => a.ImageUrl).HasMaxLength(250);
        
        builder
            .HasMany(a => a.Books)
            .WithOne(b => b.Author)
            .HasForeignKey(b => b.AuthorId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}