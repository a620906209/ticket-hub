using Microsoft.EntityFrameworkCore;
using ProjectC.Application.Common.Interfaces;

namespace ProjectC.Application.Organizers.GetMyOrganizers;

public sealed class GetMyOrganizersHandler
{
    private readonly IApplicationDbContext _dbContext;

    public GetMyOrganizersHandler(IApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyList<OrganizerSummaryDto>> HandleAsync(Guid memberId, CancellationToken cancellationToken)
    {
        return await _dbContext.OrganizerMembers
            .Where(om => om.MemberId == memberId)
            .Join(_dbContext.Organizers, om => om.OrganizerId, o => o.Id, (om, o) => o)
            .Select(o => new OrganizerSummaryDto(o.Id, o.Name, o.Status.ToString()))
            .ToListAsync(cancellationToken);
    }
}
