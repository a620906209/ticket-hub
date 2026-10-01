using Microsoft.EntityFrameworkCore;
using ProjectC.Domain.Members;

namespace ProjectC.Infrastructure.Persistence.Repositories;

public class MemberDisplayNameReader : IMemberDisplayNameReader
{
    private readonly ApplicationDbContext _dbContext;

    public MemberDisplayNameReader(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyDictionary<Guid, string>> GetDisplayNamesByIdsAsync(IReadOnlyList<Guid> memberIds, CancellationToken cancellationToken)
    {
        if (memberIds.Count == 0)
            return new Dictionary<Guid, string>();

        return await _dbContext.Members
            .AsNoTracking()
            .Where(m => memberIds.Contains(m.Id))
            .Select(m => new { m.Id, m.DisplayName })
            .ToDictionaryAsync(m => m.Id, m => m.DisplayName, cancellationToken);
    }
}
