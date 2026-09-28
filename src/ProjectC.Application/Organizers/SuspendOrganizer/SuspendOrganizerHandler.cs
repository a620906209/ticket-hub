using Microsoft.EntityFrameworkCore;
using ProjectC.Application.Common;
using ProjectC.Application.Common.Interfaces;

namespace ProjectC.Application.Organizers.SuspendOrganizer;

public sealed class SuspendOrganizerHandler
{
    private readonly IApplicationDbContext _dbContext;

    public SuspendOrganizerHandler(IApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<Result> HandleAsync(Guid organizerId, CancellationToken cancellationToken)
    {
        var organizer = await _dbContext.Organizers.FirstOrDefaultAsync(o => o.Id == organizerId, cancellationToken);
        if (organizer is null)
        {
            return Result.Failure(Error.NotFound($"Organizer '{organizerId}' was not found."));
        }

        try
        {
            organizer.Suspend();
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure(Error.Conflict(ex.Message));
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
