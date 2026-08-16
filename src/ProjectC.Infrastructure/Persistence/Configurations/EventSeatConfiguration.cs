using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProjectC.Domain.Events;
using ProjectC.Domain.Venues;

namespace ProjectC.Infrastructure.Persistence.Configurations;

public class EventSeatConfiguration : IEntityTypeConfiguration<EventSeat>
{
    public void Configure(EntityTypeBuilder<EventSeat> builder)
    {
        builder.ToTable("EventSeats");

        builder.HasKey(es => es.Id);

        builder.Property(es => es.EventId).IsRequired();
        builder.Property(es => es.SeatId).IsRequired();

        // 反映「同一活動內每個座位樣板只對應一筆 EventSeat」的唯一性。
        builder.HasIndex(es => new { es.EventId, es.SeatId }).IsUnique();

        // EventSeat 只用純量欄位參照 Event／Seat，沒有 navigation，一樣用 HasOne<T>().WithMany() 建立 FK 約束。
        builder.HasOne<Event>()
            .WithMany()
            .HasForeignKey(es => es.EventId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Seat>()
            .WithMany()
            .HasForeignKey(es => es.SeatId)
            .OnDelete(DeleteBehavior.Restrict);

        // 依 seat-reservation spec，這三個欄位刻意不對外暴露為 public 屬性，
        // 只能透過 backing field mapping 直接對應到私有欄位。
        builder.Property<Guid?>("_heldByOrderId").HasColumnName("HeldByOrderId");
        builder.Property<DateTime?>("_heldUntilUtc").HasColumnName("HeldUntilUtc");
        builder.Property<Guid?>("_soldByOrderId").HasColumnName("SoldByOrderId");
    }
}
