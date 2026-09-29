using FluentAssertions;
using ProjectC.Application.Common;
using ProjectC.Application.Organizers.SuspendOrganizer;
using ProjectC.Application.Tests.TestSupport;
using ProjectC.Domain.Organizers;

namespace ProjectC.Application.Tests.Organizers.SuspendOrganizer;

public class SuspendOrganizerHandlerTests
{
    private readonly FakeApplicationDbContext _dbContext = new();
    private readonly SuspendOrganizerHandler _handler;

    public SuspendOrganizerHandlerTests()
    {
        _handler = new SuspendOrganizerHandler(_dbContext);
    }

    private Domain.Organizers.Organizer SeedOrganizer(OrganizerStatus status)
    {
        var applicantId = Guid.NewGuid();
        var organizer = Domain.Organizers.Organizer.Apply(Guid.NewGuid(), "Org", applicantId, DateTime.UtcNow);
        if (status is OrganizerStatus.Approved or OrganizerStatus.Suspended)
        {
            organizer.Approve(applicantId, DateTime.UtcNow);
        }
        if (status == OrganizerStatus.Suspended)
        {
            organizer.Suspend();
        }
        if (status == OrganizerStatus.Rejected)
        {
            organizer.Reject(applicantId, DateTime.UtcNow);
        }
        _dbContext.OrganizerData.Add(organizer);
        return organizer;
    }

    // ORG-SUSPEND-001
    [Fact]
    public async Task HandleAsync_WhenApproved_TransitionsToSuspended()
    {
        var organizer = SeedOrganizer(OrganizerStatus.Approved);

        var result = await _handler.HandleAsync(organizer.Id, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        organizer.Status.Should().Be(OrganizerStatus.Suspended);
    }

    // ORG-SUSPEND-002
    [Theory]
    [InlineData(OrganizerStatus.Pending)]
    [InlineData(OrganizerStatus.Rejected)]
    [InlineData(OrganizerStatus.Suspended)]
    public async Task HandleAsync_WhenNotApproved_ReturnsConflictWithoutChangingStatus(OrganizerStatus status)
    {
        var organizer = SeedOrganizer(status);

        var result = await _handler.HandleAsync(organizer.Id, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Conflict);
        organizer.Status.Should().Be(status);
    }

    [Fact]
    public async Task HandleAsync_WhenOrganizerDoesNotExist_ReturnsNotFound()
    {
        var result = await _handler.HandleAsync(Guid.NewGuid(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.NotFound);
    }
}
