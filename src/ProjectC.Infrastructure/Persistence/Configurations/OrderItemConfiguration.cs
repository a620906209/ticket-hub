using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProjectC.Domain.Events;
using ProjectC.Domain.Orders;
using ProjectC.Domain.Tickets;

namespace ProjectC.Infrastructure.Persistence.Configurations;

public class OrderItemConfiguration : IEntityTypeConfiguration<OrderItem>
{
    public void Configure(EntityTypeBuilder<OrderItem> builder)
    {
        builder.ToTable("OrderItems");

        builder.HasKey(i => i.Id);

        // EventSeatId 改為可為 null（計數行項沒有座位）；TicketTypeId 也是 nullable——
        // 既有舊列不回填（design.md Migration Plan），新建立的 OrderItem 一律由 domain 建構子保證有值。
        builder.Property(i => i.EventSeatId);
        builder.Property(i => i.TicketTypeId);
        builder.Property(i => i.Quantity).IsRequired().HasDefaultValue(1);
        builder.Property(i => i.UnitPrice).IsRequired().HasColumnType("decimal(18,2)");

        // OrderId shadow FK 的關聯設定在 OrderConfiguration（Order.Items 那端），這裡不重複設定。

        // OrderItem 只用 EventSeatId／TicketTypeId 這兩個純量欄位參照對應 entity，沒有 navigation，
        // 一樣用 HasOne<T>().WithMany() 建立 FK 約束；兩者都是 nullable FK。
        builder.HasOne<EventSeat>()
            .WithMany()
            .HasForeignKey(i => i.EventSeatId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<TicketType>()
            .WithMany()
            .HasForeignKey(i => i.TicketTypeId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
