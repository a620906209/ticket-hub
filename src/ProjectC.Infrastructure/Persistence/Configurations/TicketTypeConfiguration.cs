using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProjectC.Domain.Events;
using ProjectC.Domain.Tickets;

namespace ProjectC.Infrastructure.Persistence.Configurations;

public class TicketTypeConfiguration : IEntityTypeConfiguration<TicketType>
{
    public void Configure(EntityTypeBuilder<TicketType> builder)
    {
        // check constraint 守住決策 1 的建構不變量：RequiresSeat = true ⟺ AvailableQuantity = null。
        // AvailableQuantity 用 >= 0（不是 > 0）——庫存賣完時 0 是合法值，初始值必須為正整數的規則
        // 留給 domain 建構子／CreateTicketTypeRequestValidator 負責（design.md Migration Plan 第 5 點）。
        builder.ToTable("TicketTypes", t => t.HasCheckConstraint(
            "CK_TicketTypes_RequiresSeat_AvailableQuantity",
            """("RequiresSeat" = TRUE AND "AvailableQuantity" IS NULL) OR ("RequiresSeat" = FALSE AND "AvailableQuantity" >= 0)"""));

        builder.HasKey(t => t.Id);

        builder.Property(t => t.EventId).IsRequired();
        builder.Property(t => t.ZoneCode).IsRequired().HasMaxLength(50);
        builder.Property(t => t.Price).IsRequired().HasColumnType("decimal(18,2)");
        builder.Property(t => t.RequiresSeat).IsRequired().HasDefaultValue(true);
        builder.Property(t => t.AvailableQuantity);

        // TicketType 只用 EventId 這個純量欄位參照 Event，沒有 navigation，一樣用 HasOne<T>().WithMany() 建立 FK 約束。
        builder.HasOne<Event>()
            .WithMany()
            .HasForeignKey(t => t.EventId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
