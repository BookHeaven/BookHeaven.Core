using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BookHeaven.Core.Entities;

public partial class BookProgress
{
    public Guid BookProgressId { get; set; }
    public Guid BookId { get; set; }
    public Guid ProfileId { get; set; }
    public int Chapter { get; set; }
    [Obsolete("This property is obsolete and it will be removed in the future.")]
    public int Page { get; set; }
    public double ChapterProgress { get; set; }
    [Obsolete("This property is obsolete and it will be removed in the future.")]
    public int PageCount { get; set; }
    [Obsolete("This property is obsolete and it will be removed in the future.")]
    public int? PageCountPrev { get; set; }
    [Obsolete("This property is obsolete and it will be removed in the future.")]
    public int? PageCountNext { get; set; }
    public int BookWordCount { get; set; }
    public decimal Progress { get; set; }
    public DateTimeOffset? StartDate { get; set; }
    public DateTimeOffset? EndDate { get; set; }
    public DateTimeOffset? LastRead { get; set; }
    public TimeSpan ElapsedTime { get; set; } = TimeSpan.Zero;
    
    [JsonIgnore]
    public Book? Book { get; set; }
    [JsonIgnore]
    public Profile? Profile { get; set; }

}

internal class BookProgressConfig : IEntityTypeConfiguration<BookProgress>
{
    public void Configure(EntityTypeBuilder<BookProgress> builder)
    {
        builder.HasKey(bp => bp.BookProgressId);
        builder.Property(bp => bp.BookProgressId).ValueGeneratedOnAdd();
        
        builder.Property(bp => bp.ChapterProgress)
            .HasDefaultValue(0.0);
        
        builder
            .HasOne(bp => bp.Book)
            .WithMany(b => b.Progresses)
            .HasForeignKey(bp => bp.BookId)
            .OnDelete(DeleteBehavior.Cascade);
        
        builder
            .HasOne(bp => bp.Profile)
            .WithMany(p => p.BooksProgress)
            .HasForeignKey(bp => bp.ProfileId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}