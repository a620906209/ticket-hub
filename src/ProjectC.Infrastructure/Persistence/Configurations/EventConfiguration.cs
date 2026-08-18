using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProjectC.Domain.Events;
using ProjectC.Domain.Members;
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
        builder.Property(e => e.CreatedByMemberId);
        builder.Property(e => e.CreatedAtUtc);

        // Event 只用純量欄位參照 Venue／SeatMap，沒有 navigation，一樣用 HasOne<T>().WithMany() 建立 FK 約束。
        builder.HasOne<Venue>()
            .WithMany()
            .HasForeignKey(e => e.VenueId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<SeatMap>()
            .WithMany()
            .HasForeignKey(e => e.SeatMapId)
            .OnDelete(DeleteBehavior.Restrict);

        // CreatedByMemberId 是稽核用的建立者參照，nullable（本次功能上線前的舊活動沒有這筆紀錄）；
        // Restrict 是刻意的——稽核紀錄不該因為刪除會員而憑空消失（見 design.md 決策 4，本專案會員
        // 目前也只能停用、沒有刪除功能，這個約束是防未來萬一新增刪除功能時的保護）。
        builder.HasOne<Member>()
            .WithMany()
            .HasForeignKey(e => e.CreatedByMemberId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
