using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BookHeaven.Core.Entities;

public class ReadingHistoryEntry
{
    public Guid ReadingHistoryEntryId { get; set; }
    public Guid ProfileId { get; set; }
    public Guid BookId { get; set; }
    
    public DateTimeOffset DateStarted { get; set; }
    public DateTimeOffset DateFinished { get; set; }
    public TimeSpan ElapsedTime { get; set; }
}

internal class ReadingHistoryEntryConfig : IEntityTypeConfiguration<ReadingHistoryEntry>
{
    public void Configure(EntityTypeBuilder<ReadingHistoryEntry> builder)
    {
        builder.HasKey(rh => rh.ReadingHistoryEntryId);
        builder.Property(rh => rh.ReadingHistoryEntryId).ValueGeneratedOnAdd();
        
        builder.HasOne<Book>()
            .WithMany()
            .HasForeignKey(rh => rh.BookId)
            .OnDelete(DeleteBehavior.Cascade);
        
        builder.HasOne<Profile>()
            .WithMany()
            .HasForeignKey(rh => rh.ProfileId)
            .OnDelete(DeleteBehavior.Cascade);
        
        builder.HasIndex(rh => new { rh.ProfileId, rh.BookId, rh.DateStarted, rh.DateFinished })
            .IsUnique();
    }
}