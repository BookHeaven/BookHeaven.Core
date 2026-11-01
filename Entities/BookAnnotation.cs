using System.Text.Json.Serialization;
using BookHeaven.Core.Entities.Base;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BookHeaven.Core.Entities;

public class BookAnnotation : BaseEntity
{
    public Guid BookAnnotationId { get; set; }
    public Guid BookId { get; set; }
    public Guid ProfileId { get; set; }
    public int ChapterIndex { get; set; }
    public string ChapterTitle { get; set; } = null!;
    public int StartOffset { get; set; }
    public int EndOffset { get; set; }
    public string Text { get; set; } = null!;
    public string? Note { get; set; }

    [JsonIgnore]
    public Book? Book { get; set; }

    [JsonIgnore]
    public Profile? Profile { get; set; }
}

internal class BookAnnotationConfig : BaseEntityConfig<BookAnnotation>
{
    public override void Configure(EntityTypeBuilder<BookAnnotation> builder)
    {
        base.Configure(builder);
        builder.HasKey(annotation => annotation.BookAnnotationId);
        builder.Property(annotation => annotation.BookAnnotationId).ValueGeneratedOnAdd();

        builder.Property(annotation => annotation.ChapterTitle)
            .IsRequired()
            .HasMaxLength(512);
        builder.Property(annotation => annotation.Text)
            .HasMaxLength(2048)
            .IsRequired();
        builder.Property(annotation => annotation.Note)
            .HasMaxLength(2048);

        builder.HasIndex(annotation => new
        {
            annotation.ProfileId,
            annotation.BookId,
            annotation.ChapterIndex,
            annotation.StartOffset
        });

        builder.HasOne(annotation => annotation.Book)
            .WithMany()
            .HasForeignKey(annotation => annotation.BookId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(annotation => annotation.Profile)
            .WithMany()
            .HasForeignKey(annotation => annotation.ProfileId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
