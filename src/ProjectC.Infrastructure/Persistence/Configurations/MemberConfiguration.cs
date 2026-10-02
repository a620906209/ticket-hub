using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProjectC.Domain.Members;

namespace ProjectC.Infrastructure.Persistence.Configurations;

public class MemberConfiguration : IEntityTypeConfiguration<Member>
{
    public void Configure(EntityTypeBuilder<Member> builder)
    {
        // 兩欄同時為 null 或同時有值：實名寫入不經過 Entity setter（條件式 ExecuteUpdateAsync），
        // 由資料庫守住「兩欄須同時有值」（real-name-verification design.md 決策 1）。
        builder.ToTable("Members", t => t.HasCheckConstraint(
            "CK_Members_RealName_NationalIdLast4",
            """("RealName" IS NULL) = ("NationalIdLast4" IS NULL)"""));

        builder.HasKey(m => m.Id);

        builder.Property(m => m.Email).IsRequired().HasMaxLength(256);
        builder.HasIndex(m => m.Email).IsUnique();

        builder.Property(m => m.DisplayName).IsRequired().HasMaxLength(100);
        builder.Property(m => m.PasswordHash).IsRequired();
        builder.Property(m => m.Role).IsRequired();
        builder.Property(m => m.IsActive).IsRequired();
        builder.Property(m => m.RealName).HasMaxLength(50);
        builder.Property(m => m.NationalIdLast4).HasMaxLength(4).IsFixedLength();
    }
}
