using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProjectC.Domain.Venues;

namespace ProjectC.Infrastructure.Persistence.Configurations;

public class SeatMapConfiguration : IEntityTypeConfiguration<SeatMap>
{
    public void Configure(EntityTypeBuilder<SeatMap> builder)
    {
        builder.ToTable("SeatMaps");

        builder.HasKey(m => m.Id);

        builder.Property(m => m.VenueId).IsRequired();

        // SeatMap 只用 VenueId 這個純量欄位參照 Venue，Domain 沒有 Venue navigation，
        // 用 HasOne<T>().WithMany() 不需要 navigation property 也能建立真正的資料庫 FK 約束。
        builder.HasOne<Venue>()
            .WithMany()
            .HasForeignKey(m => m.VenueId)
            .OnDelete(DeleteBehavior.Restrict);

        // SeatMap.Seats 是包住 private 欄位 _seats 的唯讀 computed property，沒有 public setter，
        // 所以 navigation 要指定用 field access mode，EF 才能把讀出來的 Seat 塞回 _seats。
        builder.HasMany(m => m.Seats)
            .WithOne()
            .HasForeignKey(s => s.SeatMapId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(m => m.Seats).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
