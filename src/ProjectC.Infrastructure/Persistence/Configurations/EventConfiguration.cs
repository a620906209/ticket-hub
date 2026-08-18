using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProjectC.Domain.Events;
using ProjectC.Domain.Venues;

namespace ProjectC.Infrastructure.Persistence.Configurations;

public class EventConfiguration : IEntityTypeConfiguration<Event>
{
    public void Configure(EntityTypeBuilder<Event> builder)
    {
        builder.ToTable("Events");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Title).IsRequired().HasMaxLength(200);
        builder.Property(e => e.StartAtUtc).IsRequired();
        builder.Property(e => e.VenueId).IsRequired();
        builder.Property(e => e.SeatMapId).IsRequired();
        builder.Property(e => e.Description).HasMaxLength(2000);
        builder.Property(e => e.PosterUrl).HasMaxLength(500);
        builder.Property(e => e.MaxTicketsPerOrder);

        // Event 只用純量欄位參照 Venue／SeatMap，沒有 navigation，一樣用 HasOne<T>().WithMany() 建立 FK 約束。
        builder.HasOne<Venue>()
            .WithMany()
            .HasForeignKey(e => e.VenueId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<SeatMap>()
            .WithMany()
            .HasForeignKey(e => e.SeatMapId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
