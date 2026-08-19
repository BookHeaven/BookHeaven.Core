using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BookHeaven.Core.Entities;

public class KoreaderProgress
{
    public Guid ProfileId { get; set; }
    public string DocumentHash { get; set; } = string.Empty;
    public string DeviceName { get; set; }  = string.Empty;
    public string DeviceId { get; set; }   = string.Empty;
    public decimal Percentage { get; set; }
    public string Progress { get; set; }  = string.Empty;
    public DateTime Timestamp { get; set; }
}

internal class KoreaderProgressConfig : IEntityTypeConfiguration<KoreaderProgress>
{
    public void Configure(EntityTypeBuilder<KoreaderProgress> builder)
    {
        builder.HasKey(kp => new { kp.ProfileId, kp.DocumentHash });
        builder.Property(kp => kp.DocumentHash).HasMaxLength(64);
        builder.Property(kp => kp.DeviceName).HasMaxLength(100);
        builder.Property(kp => kp.DeviceId).HasMaxLength(100);
        builder.Property(kp => kp.Progress).HasMaxLength(100);
    }
}