using Microsoft.EntityFrameworkCore;
using ProjectC.Application.Common;
using ProjectC.Application.Common.Interfaces;

namespace ProjectC.Application.Organizers.ReviewOrganizer;

public sealed class ReviewOrganizerHandler
{
    private readonly IApplicationDbContext _dbContext;
    private readonly IDateTimeProvider _dateTimeProvider;

    public ReviewOrganizerHandler(IApplicationDbContext dbContext, IDateTimeProvider dateTimeProvider)
    {
        _dbContext = dbContext;
        _dateTimeProvider = dateTimeProvider;
    }

    public async Task<Result> ApproveAsync(Guid organizerId, Guid reviewedByMemberId, CancellationToken cancellationToken)
        => await ReviewAsync(organizerId, reviewedByMemberId, isApprove: true, cancellationToken);

    public async Task<Result> RejectAsync(Guid organizerId, Guid reviewedByMemberId, CancellationToken cancellationToken)
        => await ReviewAsync(organizerId, reviewedByMemberId, isApprove: false, cancellationToken);

    private async Task<Result> ReviewAsync(Guid organizerId, Guid reviewedByMemberId, bool isApprove, CancellationToken cancellationToken)
    {
        var organizer = await _dbContext.Organizers.FirstOrDefaultAsync(o => o.Id == organizerId, cancellationToken);
        if (organizer is null)
        {
            return Result.Failure(Error.NotFound($"Organizer '{organizerId}' was not found."));
        }

        try
        {
            if (isApprove)
            {
                organizer.Approve(reviewedByMemberId, _dateTimeProvider.UtcNow);
            }
            else
            {
                organizer.Reject(reviewedByMemberId, _dateTimeProvider.UtcNow);
            }
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure(Error.Conflict(ex.Message));
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
