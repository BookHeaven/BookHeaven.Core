using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BookHeaven.Core.Entities;

public class Tag
{
    public Guid TagId { get; set; }
    public string Name { get; set; } = string.Empty;
}

internal class TagConfig : IEntityTypeConfiguration<Tag>
{
    public void Configure(EntityTypeBuilder<Tag> builder)
    {
        builder.HasKey(t => t.TagId);
        builder.Property(t => t.TagId).ValueGeneratedOnAdd();
        
        builder.Property(t => t.Name).IsRequired().HasMaxLength(100);
    }
}