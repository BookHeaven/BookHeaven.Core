using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BookHeaven.Core.Entities;

public partial class Profile
{
    public Guid ProfileId { get; set; }

    public string Name { get; set; } = null!;
    public ProfileSettings? ProfileSettings { get; set; }
    [JsonIgnore]
    public List<BookProgress> BooksProgress { get; set; } = [];
}

internal class ProfileConfig : IEntityTypeConfiguration<Profile>
{
    public void Configure(EntityTypeBuilder<Profile> builder)
    {
        builder.HasKey(p => p.ProfileId);
        builder.Property(p => p.ProfileId).ValueGeneratedOnAdd();
        
        builder.HasIndex(p => p.Name).IsUnique();
        
        builder.Property(p => p.Name).IsRequired().HasMaxLength(100);
    }
}