using FluentAssertions;
using ProjectC.Application.Organizers.GetMyOrganizers;
using ProjectC.Application.Tests.TestSupport;
using ProjectC.Domain.Organizers;

namespace ProjectC.Application.Tests.Organizers.GetMyOrganizers;

public class GetMyOrganizersHandlerTests
{
    private readonly FakeApplicationDbContext _dbContext = new();
    private readonly GetMyOrganizersHandler _handler;

    public GetMyOrganizersHandlerTests()
    {
        _handler = new GetMyOrganizersHandler(_dbContext);
    }

    private Domain.Organizers.Organizer SeedOrganizer(string name, OrganizerStatus status, Guid memberId)
    {
        var organizer = Domain.Organizers.Organizer.Apply(Guid.NewGuid(), name, memberId, DateTime.UtcNow);
        if (status == OrganizerStatus.Approved)
        {
            organizer.Approve(memberId, DateTime.UtcNow);
        }
        _dbContext.OrganizerData.Add(organizer);
        _dbContext.OrganizerMemberData.Add(new OrganizerMember(Guid.NewGuid(), organizer.Id, memberId, OrganizerMemberRole.Owner));
        return organizer;
    }

    // ORG-LIST-001
    [Fact]
    public async Task HandleAsync_ReturnsOnlyOrganizersBelongingToCaller_NotOtherMembersOrganizers()
    {
        var memberId = Guid.NewGuid();
        var otherMemberId = Guid.NewGuid();
        var myOrganizer = SeedOrganizer("Mine", OrganizerStatus.Approved, memberId);
        SeedOrganizer("Someone Else's", OrganizerStatus.Approved, otherMemberId);

        var result = await _handler.HandleAsync(memberId, CancellationToken.None);

        result.Should().ContainSingle();
        result[0].Id.Should().Be(myOrganizer.Id);
    }

    // ORG-LIST-002
    [Fact]
    public async Task HandleAsync_WhenNotAMemberOfAnyOrganizer_ReturnsEmptyListWithoutError()
    {
        var result = await _handler.HandleAsync(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeEmpty();
    }
}
