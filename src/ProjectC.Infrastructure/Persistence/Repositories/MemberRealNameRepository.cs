using Microsoft.EntityFrameworkCore;
using ProjectC.Domain.Members;

namespace ProjectC.Infrastructure.Persistence.Repositories;

public class MemberRealNameRepository : IMemberRealNameRepository
{
    private readonly ApplicationDbContext _dbContext;

    public MemberRealNameRepository(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    // 以單一條件式 UPDATE 取代「讀出→檢查→SaveChanges」：兩個並發的首次登記只有一個能命中 RealName IS NULL，
    // 不會發生後寫者覆蓋前者（real-name-verification design.md 決策 2）。
    public async Task<bool> TryRegisterAsync(Guid memberId, string realName, string nationalIdLast4, CancellationToken cancellationToken)
    {
        var affectedRows = await _dbContext.Members
            .Where(m => m.Id == memberId && m.RealName == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(m => m.RealName, realName)
                .SetProperty(m => m.NationalIdLast4, nationalIdLast4),
                cancellationToken);

        return affectedRows == 1;
    }

    public async Task<MemberRealName?> GetAsync(Guid memberId, CancellationToken cancellationToken)
        => await _dbContext.Members
            .AsNoTracking()
            .Where(m => m.Id == memberId && m.RealName != null)
            .Select(m => new MemberRealName(m.RealName!, m.NationalIdLast4!))
            .FirstOrDefaultAsync(cancellationToken);
}
