using ProjectC.Domain.Members;
using ProjectC.Domain.Organizers;
using ProjectC.Infrastructure.Persistence;

namespace ProjectC.Infrastructure.Tests.TestSupport;

/// <summary>
/// Events.OrganizerId 是指向 Organizers 的 NOT NULL 外鍵，Organizer 又以外鍵指向建立者 Member——
/// 任何要把 Event 真的寫進資料庫的測試都必須先有一筆真實的 Organizer，不能只傳 <see cref="Guid.NewGuid"/>
/// （見 event-management-organizer-scoping tasks.md 3.3）。
/// </summary>
public static class OrganizerTestData
{
    public static async Task<Guid> SeedApprovedOrganizerAsync(ApplicationDbContext dbContext, CancellationToken ct = default)
    {
        var owner = Member.Register($"organizer-owner-{Guid.NewGuid():N}@example.com", "Organizer Owner", "hash");
        var organizer = Organizer.Apply(Guid.NewGuid(), "Test Organizer", owner.Id, DateTime.UtcNow);
        organizer.Approve(owner.Id, DateTime.UtcNow);

        dbContext.Members.Add(owner);
        dbContext.Organizers.Add(organizer);
        await dbContext.SaveChangesAsync(ct);

        return organizer.Id;
    }
}
