using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProjectC.Domain.Venues;

namespace ProjectC.Infrastructure.Persistence.Configurations;

public class SeatConfiguration : IEntityTypeConfiguration<Seat>
{
    public void Configure(EntityTypeBuilder<Seat> builder)
    {
        builder.ToTable("Seats");

        builder.HasKey(s => s.Id);

        builder.Property(s => s.ZoneCode).IsRequired().HasMaxLength(50);
        builder.Property(s => s.SeatNumber).IsRequired().HasMaxLength(50);

        // 反映 SeatMap.AddSeat 既有的唯一性驗證：同一座位圖內，分區代碼＋座位編號組合唯一。
        builder.HasIndex(s => new { s.SeatMapId, s.ZoneCode, s.SeatNumber }).IsUnique();
    }
}
