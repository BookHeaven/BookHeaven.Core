using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BookHeaven.Core.Entities;

public class ProfileSettings
{
    public Guid ProfileSettingsId { get; set; }

    public Guid ProfileId { get; set; }
    [JsonIgnore]
    public Profile Profile { get; set; } = null!;
    
    public decimal FontSize { get; set; } = 16;
    public decimal LineHeight { get; set; } = 1.3m;
    public decimal LetterSpacing { get; set; } = 0;
    public decimal WordSpacing { get; set; } = 0;
    public decimal ParagraphSpacing { get; set; } = 10;
    public decimal TextIndent { get; set; } = 1;
    public decimal HorizontalMargin { get; set; } = 3;
    public decimal VerticalMargin { get; set; } = 1;
    public decimal PageGap { get; set; } = 50;
    public int SelectedLayout { get; set; } = 0;
    public string SelectedFont { get; set; } = string.Empty;
}

internal class ProfileSettingsConfig : IEntityTypeConfiguration<ProfileSettings>
{
    public void Configure(EntityTypeBuilder<ProfileSettings> builder)
    {
        builder.HasKey(ps => ps.ProfileSettingsId);
        builder.Property(ps => ps.ProfileSettingsId).ValueGeneratedOnAdd();
        
        builder.Property(ps => ps.SelectedFont).HasMaxLength(100);
        
        builder
            .HasOne(ps => ps.Profile)
            .WithOne(p => p.ProfileSettings)
            .HasForeignKey<ProfileSettings>(ps => ps.ProfileId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}