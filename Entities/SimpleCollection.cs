using BookHeaven.Core.Entities.Base;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BookHeaven.Core.Entities;

public class SimpleCollection : Collection
{
    public List<Guid> BookIds { get; set; } = [];
}

internal class SimpleCollectionConfig : IEntityTypeConfiguration<SimpleCollection>
{
    public void Configure(EntityTypeBuilder<SimpleCollection> builder)
    {
        builder.HasBaseType<Collection>();
    }
}