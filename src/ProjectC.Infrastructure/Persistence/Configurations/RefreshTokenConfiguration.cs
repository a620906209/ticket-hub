using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProjectC.Domain.Authentication;
using ProjectC.Domain.Organizers;

namespace ProjectC.Infrastructure.Persistence.Configurations;

public class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.ToTable("RefreshTokens");

        builder.HasKey(t => t.Id);

        builder.Property(t => t.TokenHash).IsRequired();
        builder.HasIndex(t => t.TokenHash).IsUnique();

        builder.Property(t => t.MemberId).IsRequired();
        builder.HasIndex(t => t.MemberId);

        builder.Property(t => t.ExpiresAt).IsRequired();
        builder.Property(t => t.Status).IsRequired();
        builder.Property(t => t.PreviousTokenId);
        builder.Property(t => t.OrganizerId);

        // Restrict（非 Cascade）：Organizer 本次沒有刪除功能，這是防未來萬一新增刪除功能時的保護，
        // 比照既有 EventConfiguration 的 CreatedByMemberId 外鍵（見 organizer-management design.md
        // Migration Plan、proposal.md Impact）。
        builder.HasOne<Organizer>()
            .WithMany()
            .HasForeignKey(t => t.OrganizerId)
            .OnDelete(DeleteBehavior.Restrict);

        // Postgres 沒有 SQL Server 風格的 rowversion 型別，改用系統欄位 xmin 做樂觀並發控制（見 design.md 決策 7）。
        builder.Property<uint>("xmin").HasColumnName("xmin").IsRowVersion();
    }
}
