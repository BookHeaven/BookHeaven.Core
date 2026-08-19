using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using BookHeaven.Core.Enums;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BookHeaven.Core.Entities.Base;

public abstract class Collection
{
    public Guid CollectionId { get; set; }
    public string Name { get; set; } = string.Empty;
    public CollectionType CollectionType { get; set; }
    public Guid? ProfileId { get; set; }
    public int SortOrder { get; set; }
    public bool Pinned { get; set; }
}

internal class CollectionConfiguration : IEntityTypeConfiguration<Collection>
{
    public void Configure(EntityTypeBuilder<Collection> builder)
    {
        builder.ToTable("Collections");
        
        builder.HasKey(c => c.CollectionId);
        builder.Property(c => c.CollectionId).ValueGeneratedOnAdd();
        
        builder.Property(c => c.Name).IsRequired().HasMaxLength(100);
        
        builder.
            HasDiscriminator<CollectionType>(nameof(Collection.CollectionType))
            .HasValue<SimpleCollection>(CollectionType.Simple)
            .HasValue<SmartCollection>(CollectionType.Smart);
    }
}