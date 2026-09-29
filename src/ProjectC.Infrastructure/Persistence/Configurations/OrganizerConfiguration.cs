using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProjectC.Domain.Members;
using ProjectC.Domain.Organizers;

namespace ProjectC.Infrastructure.Persistence.Configurations;

public class OrganizerConfiguration : IEntityTypeConfiguration<Organizer>
{
    public void Configure(EntityTypeBuilder<Organizer> builder)
    {
        builder.ToTable("Organizers");

        builder.HasKey(o => o.Id);

        builder.Property(o => o.Name).IsRequired().HasMaxLength(100);
        builder.Property(o => o.Status).IsRequired();
        builder.Property(o => o.CreatedByMemberId).IsRequired();
        builder.Property(o => o.CreatedAtUtc).IsRequired();
        builder.Property(o => o.ReviewedByMemberId);
        builder.Property(o => o.ReviewedAtUtc);

        builder.HasOne<Member>()
            .WithMany()
            .HasForeignKey(o => o.CreatedByMemberId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Member>()
            .WithMany()
            .HasForeignKey(o => o.ReviewedByMemberId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
