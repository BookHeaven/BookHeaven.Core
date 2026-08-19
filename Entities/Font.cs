using BookHeaven.Core.Entities.Base;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BookHeaven.Core.Entities;

public class Font : BaseEntity
{
    public string Family { get; set; } = null!;
    public string Style { get; set; } = string.Empty;
    public string Weight { get; set; } = string.Empty;
    public string FileName { get; set; } = null!;
}

internal class FontConfig : BaseEntityConfig<Font>
{
    public override void Configure(EntityTypeBuilder<Font> builder)
    {
        base.Configure(builder);
        builder.HasKey(f => new { f.Family, f.Style, f.Weight });
        
        builder.Property(f => f.Family).IsRequired().HasMaxLength(100);
        builder.Property(f => f.Style).IsRequired().HasMaxLength(50);
        builder.Property(f => f.Weight).IsRequired().HasMaxLength(50);
        builder.Property(f => f.FileName).IsRequired().HasMaxLength(255);
    }
}