using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BookHeaven.Core.Entities.Base;

public abstract class BaseEntity
{
    public DateTimeOffset UpdatedAt { get; set; }
}

internal abstract class BaseEntityConfig<T> : IEntityTypeConfiguration<T> where T : BaseEntity
{
    public virtual void Configure(EntityTypeBuilder<T> builder)
    {
        builder.Property(e => e.UpdatedAt)
            .IsRequired();
    }
}