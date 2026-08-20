using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProjectC.Domain.Orders;
using ProjectC.Domain.Tickets;

namespace ProjectC.Infrastructure.Persistence.Configurations;

public class TicketConfiguration : IEntityTypeConfiguration<Ticket>
{
    public void Configure(EntityTypeBuilder<Ticket> builder)
    {
        builder.ToTable("Tickets");

        builder.HasKey(t => t.Id);

        builder.Property(t => t.OrderItemId).IsRequired();
        builder.Property(t => t.Status).IsRequired();
        builder.Property(t => t.IssuedAtUtc).IsRequired();
        builder.Property(t => t.RedeemedAtUtc);

        // Ticket 只用 OrderItemId 這個純量欄位參照 OrderItem，沒有 navigation（OrderItem 沒有反向的
        // Tickets 集合），一樣用 HasOne<T>().WithMany() 建立 FK 約束，比照 OrderItemConfiguration。
        builder.HasOne<OrderItem>()
            .WithMany()
            .HasForeignKey(t => t.OrderItemId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(t => t.OrderItemId);
    }
}
