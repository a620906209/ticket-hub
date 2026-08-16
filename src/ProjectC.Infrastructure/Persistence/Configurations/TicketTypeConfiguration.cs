using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProjectC.Domain.Events;
using ProjectC.Domain.Tickets;

namespace ProjectC.Infrastructure.Persistence.Configurations;

public class TicketTypeConfiguration : IEntityTypeConfiguration<TicketType>
{
    public void Configure(EntityTypeBuilder<TicketType> builder)
    {
        builder.ToTable("TicketTypes");

        builder.HasKey(t => t.Id);

        builder.Property(t => t.EventId).IsRequired();
        builder.Property(t => t.ZoneCode).IsRequired().HasMaxLength(50);
        builder.Property(t => t.Price).IsRequired().HasColumnType("decimal(18,2)");

        // TicketType 只用 EventId 這個純量欄位參照 Event，沒有 navigation，一樣用 HasOne<T>().WithMany() 建立 FK 約束。
        builder.HasOne<Event>()
            .WithMany()
            .HasForeignKey(t => t.EventId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
