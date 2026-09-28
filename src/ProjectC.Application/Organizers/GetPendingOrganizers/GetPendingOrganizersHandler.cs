using Microsoft.EntityFrameworkCore;
using ProjectC.Application.Common.Interfaces;
using ProjectC.Domain.Organizers;

namespace ProjectC.Application.Organizers.GetPendingOrganizers;

public sealed class GetPendingOrganizersHandler
{
    private readonly IApplicationDbContext _dbContext;

    public GetPendingOrganizersHandler(IApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyList<PendingOrganizerDto>> HandleAsync(CancellationToken cancellationToken)
    {
        return await _dbContext.Organizers
            .Where(o => o.Status == OrganizerStatus.Pending)
            .Join(_dbContext.Members, o => o.CreatedByMemberId, m => m.Id, (o, m) => new PendingOrganizerDto(o.Id, o.Name, o.CreatedByMemberId, m.DisplayName, o.CreatedAtUtc))
            .ToListAsync(cancellationToken);
    }
}
