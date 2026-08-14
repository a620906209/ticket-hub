using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProjectC.Domain.Authentication;

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

        // Postgres 沒有 SQL Server 風格的 rowversion 型別，改用系統欄位 xmin 做樂觀並發控制（見 design.md 決策 7）。
        builder.Property<uint>("xmin").HasColumnName("xmin").IsRowVersion();
    }
}
