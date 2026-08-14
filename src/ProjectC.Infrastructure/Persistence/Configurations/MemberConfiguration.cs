using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProjectC.Domain.Members;

namespace ProjectC.Infrastructure.Persistence.Configurations;

public class MemberConfiguration : IEntityTypeConfiguration<Member>
{
    public void Configure(EntityTypeBuilder<Member> builder)
    {
        builder.ToTable("Members");

        builder.HasKey(m => m.Id);

        builder.Property(m => m.Email).IsRequired().HasMaxLength(256);
        builder.HasIndex(m => m.Email).IsUnique();

        builder.Property(m => m.DisplayName).IsRequired().HasMaxLength(100);
        builder.Property(m => m.PasswordHash).IsRequired();
        builder.Property(m => m.Role).IsRequired();
        builder.Property(m => m.IsActive).IsRequired();
    }
}
