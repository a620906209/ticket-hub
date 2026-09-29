using FluentAssertions;
using ProjectC.Application.Common;
using ProjectC.Application.Organizers.ReviewOrganizer;
using ProjectC.Application.Tests.TestSupport;
using ProjectC.Domain.Organizers;

namespace ProjectC.Application.Tests.Organizers.ReviewOrganizer;

public class ReviewOrganizerHandlerTests
{
    private readonly FakeApplicationDbContext _dbContext = new();
    private readonly FakeDateTimeProvider _dateTimeProvider = new();
    private readonly ReviewOrganizerHandler _handler;

    public ReviewOrganizerHandlerTests()
    {
        _handler = new ReviewOrganizerHandler(_dbContext, _dateTimeProvider);
    }

    private Domain.Organizers.Organizer SeedOrganizer(OrganizerStatus status)
    {
        var applicantId = Guid.NewGuid();
        var organizer = Domain.Organizers.Organizer.Apply(Guid.NewGuid(), "Org", applicantId, _dateTimeProvider.UtcNow);
        switch (status)
        {
            case OrganizerStatus.Approved:
                organizer.Approve(applicantId, _dateTimeProvider.UtcNow);
                break;
            case OrganizerStatus.Rejected:
                organizer.Reject(applicantId, _dateTimeProvider.UtcNow);
                break;
            case OrganizerStatus.Suspended:
                organizer.Approve(applicantId, _dateTimeProvider.UtcNow);
                organizer.Suspend();
                break;
        }
        _dbContext.OrganizerData.Add(organizer);
        return organizer;
    }

    // ORG-REVIEW-002
    [Fact]
    public async Task ApproveAsync_WhenPending_TransitionsToApprovedAndRecordsReviewer()
    {
        var organizer = SeedOrganizer(OrganizerStatus.Pending);
        var reviewerId = Guid.NewGuid();
        _dateTimeProvider.UtcNow = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

        var result = await _handler.ApproveAsync(organizer.Id, reviewerId, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        organizer.Status.Should().Be(OrganizerStatus.Approved);
        organizer.ReviewedByMemberId.Should().Be(reviewerId);
        organizer.ReviewedAtUtc.Should().Be(_dateTimeProvider.UtcNow);
    }

    // ORG-REVIEW-003
    [Fact]
    public async Task RejectAsync_WhenPending_TransitionsToRejectedAndRecordsReviewer()
    {
        var organizer = SeedOrganizer(OrganizerStatus.Pending);
        var reviewerId = Guid.NewGuid();

        var result = await _handler.RejectAsync(organizer.Id, reviewerId, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        organizer.Status.Should().Be(OrganizerStatus.Rejected);
        organizer.ReviewedByMemberId.Should().Be(reviewerId);
        organizer.ReviewedAtUtc.Should().NotBeNull();
    }

    // ORG-REVIEW-005
    [Theory]
    [InlineData(OrganizerStatus.Approved)]
    [InlineData(OrganizerStatus.Rejected)]
    [InlineData(OrganizerStatus.Suspended)]
    public async Task ApproveAsync_WhenNotPending_ReturnsConflictWithoutChangingStatus(OrganizerStatus status)
    {
        var organizer = SeedOrganizer(status);

        var result = await _handler.ApproveAsync(organizer.Id, Guid.NewGuid(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Conflict);
        organizer.Status.Should().Be(status);
    }

    // ORG-REVIEW-005
    [Theory]
    [InlineData(OrganizerStatus.Approved)]
    [InlineData(OrganizerStatus.Rejected)]
    [InlineData(OrganizerStatus.Suspended)]
    public async Task RejectAsync_WhenNotPending_ReturnsConflictWithoutChangingStatus(OrganizerStatus status)
    {
        var organizer = SeedOrganizer(status);

        var result = await _handler.RejectAsync(organizer.Id, Guid.NewGuid(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Conflict);
        organizer.Status.Should().Be(status);
    }

    [Fact]
    public async Task ApproveAsync_WhenOrganizerDoesNotExist_ReturnsNotFound()
    {
        var result = await _handler.ApproveAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.NotFound);
    }
}
