using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProjectC.Domain.Events;
using ProjectC.Domain.Members;
using ProjectC.Domain.Orders;

namespace ProjectC.Infrastructure.Persistence.Configurations;

public class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> builder)
    {
        builder.ToTable("Orders");

        builder.HasKey(o => o.Id);

        builder.Property(o => o.EventId).IsRequired();
        builder.Property(o => o.BuyerId).IsRequired();
        builder.Property(o => o.HeldUntilUtc).IsRequired();

        // Order 只用 EventId 這個純量欄位參照 Event，沒有 navigation，一樣用 HasOne<T>().WithMany() 建立 FK 約束。
        builder.HasOne<Event>()
            .WithMany()
            .HasForeignKey(o => o.EventId)
            .OnDelete(DeleteBehavior.Restrict);

        // BuyerId 同理參照 Member，沒有 navigation（見 ticketing-purchase design.md 決策 1）。
        builder.HasOne<Member>()
            .WithMany()
            .HasForeignKey(o => o.BuyerId)
            .OnDelete(DeleteBehavior.Restrict);

        // Status 只會被賦值 Pending/Paid/Cancelled；Expired 是 GetStatus(now) 查詢時推導出來的值，
        // 從不落地寫入（見 Order.GetStatus 與 seat-reservation/ticket-ordering spec 的既有不變條件）。
        builder.Property(o => o.Status).IsRequired();

        // Order.Items 是包住 private 欄位 _items 的唯讀 computed property，沒有 public setter，
        // navigation 要指定用 field access mode；OrderItem 沒有 OrderId 屬性，關聯用 shadow FK 處理。
        builder.HasMany(o => o.Items)
            .WithOne()
            .HasForeignKey("OrderId")
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(o => o.Items).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
