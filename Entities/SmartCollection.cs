using System.Text.Json;
using BookHeaven.Core.Entities.Base;
using BookHeaven.Core.Entities.Utilities;
using BookHeaven.Core.Enums;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace BookHeaven.Core.Entities;

public class SmartCollection : Collection
{
    public FilterSet<Guid> Authors { get; set; } = new();
    public FilterSet<Guid> Series { get; set; } = new();
    public FilterSet<Guid> Tags { get; set; } = new();
    public FilterSet<BookStatus> Statuses { get; set; } = new();
}

internal class SmartCollectionConfiguration : IEntityTypeConfiguration<SmartCollection>
{
    public void Configure(EntityTypeBuilder<SmartCollection> builder)
    {
        builder.HasBaseType<Collection>();
        
        var guidFilterSetConverter = new ValueConverter<FilterSet<Guid>, string>(
            v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
            v => JsonSerializer.Deserialize<FilterSet<Guid>>(v, (JsonSerializerOptions?)null) ?? new FilterSet<Guid>()
        );
        foreach (var prop in new[] { nameof(SmartCollection.Authors), nameof(SmartCollection.Series), nameof(SmartCollection.Tags) })
        {
            builder
                .Property(prop)
                .HasConversion(guidFilterSetConverter)
                .HasMaxLength(4000);
        }
        builder
            .Property(x => x.Statuses)
            .HasConversion(
                v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                v => JsonSerializer.Deserialize<FilterSet<BookStatus>>(v, (JsonSerializerOptions?)null) ?? new FilterSet<BookStatus>()
            )
            .HasMaxLength(4000);
    }
}