using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;
using BookHeaven.Core.Entities.Base;
using BookHeaven.Core.Enums;
using BookHeaven.Core.Extensions;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BookHeaven.Core.Entities;

public partial class Book : BaseEntity
{
    public Guid BookId { get; set; }
    public string? Title { get; set; }
    public Guid? AuthorId { get; set; }
    public Guid? SeriesId { get; set; }
    public decimal? SeriesIndex { get; set; }
    public string? Publisher { get; set; }
    public DateTime? PublishedDate { get; set; }
    public string? Description { get; set; }
    public string? ISBN10 { get; set; }
    public string? ISBN13 { get; set; }
    public string? ASIN { get; set; }
    public string? UUID { get; set; }
    public string? Language { get; set; }
    public EbookFormat Format { get; set; } = EbookFormat.Epub;
    public string FileHash { get; set; } = null!;
    
    public Author? Author { get; set; }
    public Series? Series { get; set; }
    
    public virtual List<Tag> Tags { get; set; } = [];

    [JsonIgnore]
    public virtual List<BookProgress> Progresses { get; set; } = [];
}

internal class BookConfig : BaseEntityConfig<Book>
{
    public override void Configure(EntityTypeBuilder<Book> builder)
    {
        base.Configure(builder);
        builder.HasKey(b => b.BookId);
        builder.Property(b => b.BookId).ValueGeneratedOnAdd();

        builder.HasIndex(b => b.FileHash);
        
        builder
            .Property(b => b.Format)
            .HasDefaultValue(EbookFormat.Epub)
            .HasSentinel(EbookFormat.None);
        
        builder
            .HasOne(b => b.Author)
            .WithMany(a => a.Books)
            .HasForeignKey(b => b.AuthorId)
            .OnDelete(DeleteBehavior.Restrict);
        
        builder
            .HasOne(b => b.Series)
            .WithMany(s => s.Books)
            .HasForeignKey(b => b.SeriesId)
            .OnDelete(DeleteBehavior.SetNull);
        
        builder
            .HasMany(b => b.Tags)
            .WithMany();
    }
}