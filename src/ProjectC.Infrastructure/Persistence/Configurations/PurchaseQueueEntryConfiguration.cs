using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProjectC.Domain.Events;
using ProjectC.Domain.Members;
using ProjectC.Domain.PurchaseQueue;

namespace ProjectC.Infrastructure.Persistence.Configurations;

public class PurchaseQueueEntryConfiguration : IEntityTypeConfiguration<PurchaseQueueEntry>
{
    public void Configure(EntityTypeBuilder<PurchaseQueueEntry> builder)
    {
        builder.ToTable("PurchaseQueueEntries");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.EventId).IsRequired();
        builder.Property(e => e.MemberId).IsRequired();
        builder.Property(e => e.JoinedAtUtc).IsRequired();
        builder.Property(e => e.AdmittedAtUtc);
        builder.Property(e => e.AdmissionExpiresAtUtc);

        // Status MUST 以字串轉換儲存，不用 EF Core 預設的 integer 儲存——下面 partial unique index 的
        // WHERE 子句以字面字串比對，欄位實際型別須與之一致（rate-limiting-queue design.md 決策 3）。
        builder.Property(e => e.Status).IsRequired().HasConversion<string>().HasMaxLength(20);

        // 供背景推進服務與排隊狀態查詢的排序需求（design.md 決策 3）。
        builder.HasIndex(e => new { e.EventId, e.Status, e.JoinedAtUtc, e.Id });

        // 同一會員對同一活動同時最多只有一筆「進行中」（Waiting 或 Admitted）紀錄；Completed／Expired
        // 屬於歷史紀錄不受此唯一性約束，允許逾時或完成後重新加入排隊（design.md 決策 3）。
        builder.HasIndex(e => new { e.EventId, e.MemberId })
            .IsUnique()
            .HasFilter("\"Status\" IN ('Waiting', 'Admitted')");

        builder.HasOne<Event>()
            .WithMany()
            .HasForeignKey(e => e.EventId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Member>()
            .WithMany()
            .HasForeignKey(e => e.MemberId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
