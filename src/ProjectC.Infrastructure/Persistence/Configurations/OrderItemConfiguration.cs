using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProjectC.Domain.Events;
using ProjectC.Domain.Orders;

namespace ProjectC.Infrastructure.Persistence.Configurations;

public class OrderItemConfiguration : IEntityTypeConfiguration<OrderItem>
{
    public void Configure(EntityTypeBuilder<OrderItem> builder)
    {
        builder.ToTable("OrderItems");

        builder.HasKey(i => i.Id);

        builder.Property(i => i.EventSeatId).IsRequired();
        builder.Property(i => i.UnitPrice).IsRequired().HasColumnType("decimal(18,2)");

        // OrderId shadow FK 的關聯設定在 OrderConfiguration（Order.Items 那端），這裡不重複設定。

        // OrderItem 只用 EventSeatId 這個純量欄位參照 EventSeat，沒有 navigation，一樣用 HasOne<T>().WithMany() 建立 FK 約束。
        builder.HasOne<EventSeat>()
            .WithMany()
            .HasForeignKey(i => i.EventSeatId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
