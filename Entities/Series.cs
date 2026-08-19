using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;
using BookHeaven.Core.Extensions;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BookHeaven.Core.Entities;

public partial class Series
{
    public Guid SeriesId { get; set; }
    public string? Name { get; set; }
    [JsonIgnore]
    public List<Book> Books { get; set; } = [];
}

internal class SeriesConfig : IEntityTypeConfiguration<Series>
{
    public void Configure(EntityTypeBuilder<Series> builder)
    {
        builder.HasKey(s => s.SeriesId);
        builder.Property(s => s.SeriesId).ValueGeneratedOnAdd();
        
        builder.Property(t => t.Name).HasMaxLength(150);
        
        builder
            .HasMany(s => s.Books)
            .WithOne(b => b.Series)
            .HasForeignKey(b => b.SeriesId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}