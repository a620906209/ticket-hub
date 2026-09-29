using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProjectC.Domain.Members;
using ProjectC.Domain.Organizers;

namespace ProjectC.Infrastructure.Persistence.Configurations;

public class OrganizerMemberConfiguration : IEntityTypeConfiguration<OrganizerMember>
{
    public void Configure(EntityTypeBuilder<OrganizerMember> builder)
    {
        builder.ToTable("OrganizerMembers");

        builder.HasKey(om => om.Id);

        builder.Property(om => om.OrganizerId).IsRequired();
        builder.Property(om => om.MemberId).IsRequired();
        builder.Property(om => om.Role).IsRequired();

        // 防重複成員列的資料庫層保底；同時是 event-management-organizer-scoping 回填腳本
        // ON CONFLICT (OrganizerId, MemberId) 的衝突目標（見該能力 tasks.md 3.2a）。
        builder.HasIndex(om => new { om.OrganizerId, om.MemberId }).IsUnique();

        builder.HasOne<Organizer>()
            .WithMany()
            .HasForeignKey(om => om.OrganizerId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Member>()
            .WithMany()
            .HasForeignKey(om => om.MemberId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
