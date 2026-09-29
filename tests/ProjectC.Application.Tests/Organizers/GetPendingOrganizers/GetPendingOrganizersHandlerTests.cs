using FluentAssertions;
using ProjectC.Application.Organizers.GetPendingOrganizers;
using ProjectC.Application.Tests.TestSupport;
using ProjectC.Domain.Members;
using ProjectC.Domain.Organizers;

namespace ProjectC.Application.Tests.Organizers.GetPendingOrganizers;

public class GetPendingOrganizersHandlerTests
{
    private readonly FakeApplicationDbContext _dbContext = new();
    private readonly GetPendingOrganizersHandler _handler;

    public GetPendingOrganizersHandlerTests()
    {
        _handler = new GetPendingOrganizersHandler(_dbContext);
    }

    private Member SeedApplicant(string displayName)
    {
        var member = Member.Register($"{Guid.NewGuid():N}@example.com", displayName, "hash");
        _dbContext.MemberData.Add(member);
        return member;
    }

    // ORG-REVIEW-001
    [Fact]
    public async Task HandleAsync_ReturnsOnlyPendingOrganizersWithApplicantInfo()
    {
        var pendingApplicant = SeedApplicant("Alice");
        var pendingOrganizer = Domain.Organizers.Organizer.Apply(Guid.NewGuid(), "Pending Org", pendingApplicant.Id, DateTime.UtcNow);
        _dbContext.OrganizerData.Add(pendingOrganizer);

        var approvedApplicant = SeedApplicant("Bob");
        var approvedOrganizer = Domain.Organizers.Organizer.Apply(Guid.NewGuid(), "Approved Org", approvedApplicant.Id, DateTime.UtcNow);
        approvedOrganizer.Approve(approvedApplicant.Id, DateTime.UtcNow);
        _dbContext.OrganizerData.Add(approvedOrganizer);

        var result = await _handler.HandleAsync(CancellationToken.None);

        result.Should().ContainSingle();
        result[0].Id.Should().Be(pendingOrganizer.Id);
        result[0].CreatedByDisplayName.Should().Be("Alice");
    }
}
