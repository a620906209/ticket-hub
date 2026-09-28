using FluentValidation;
using ProjectC.Application.Common;
using ProjectC.Application.Common.Interfaces;
using ProjectC.Domain.Organizers;

namespace ProjectC.Application.Organizers.ApplyForOrganizer;

public sealed class ApplyForOrganizerHandler
{
    private readonly IApplicationDbContext _dbContext;
    private readonly IValidator<ApplyForOrganizerRequest> _validator;
    private readonly IDateTimeProvider _dateTimeProvider;

    public ApplyForOrganizerHandler(
        IApplicationDbContext dbContext,
        IValidator<ApplyForOrganizerRequest> validator,
        IDateTimeProvider dateTimeProvider)
    {
        _dbContext = dbContext;
        _validator = validator;
        _dateTimeProvider = dateTimeProvider;
    }

    public async Task<Result<Guid>> HandleAsync(Guid memberId, ApplyForOrganizerRequest request, CancellationToken cancellationToken)
    {
        var validation = await _validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return Result<Guid>.Failure(Error.Validation(string.Join(" ", validation.Errors.Select(e => e.ErrorMessage))));
        }

        var now = _dateTimeProvider.UtcNow;
        var organizer = Organizer.Apply(Guid.NewGuid(), request.Name, memberId, now);
        var organizerMember = new OrganizerMember(Guid.NewGuid(), organizer.Id, memberId, OrganizerMemberRole.Owner);

        _dbContext.Organizers.Add(organizer);
        _dbContext.OrganizerMembers.Add(organizerMember);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Result<Guid>.Success(organizer.Id);
    }
}
