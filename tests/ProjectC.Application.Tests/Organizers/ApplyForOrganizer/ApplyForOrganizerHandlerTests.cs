using FluentAssertions;
using ProjectC.Application.Common;
using ProjectC.Application.Organizers.ApplyForOrganizer;
using ProjectC.Application.Tests.TestSupport;
using ProjectC.Domain.Organizers;

namespace ProjectC.Application.Tests.Organizers.ApplyForOrganizer;

public class ApplyForOrganizerHandlerTests
{
    private readonly FakeApplicationDbContext _dbContext = new();
    private readonly FakeDateTimeProvider _dateTimeProvider = new();
    private readonly ApplyForOrganizerHandler _handler;

    public ApplyForOrganizerHandlerTests()
    {
        _handler = new ApplyForOrganizerHandler(_dbContext, new ApplyForOrganizerRequestValidator(), _dateTimeProvider);
    }

    // ORG-APPLY-001
    [Fact]
    public async Task HandleAsync_WithValidName_CreatesPendingOrganizerAndOwnerMember()
    {
        var memberId = Guid.NewGuid();

        var result = await _handler.HandleAsync(memberId, new ApplyForOrganizerRequest("My Organizer"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var organizer = _dbContext.OrganizerData.Should().ContainSingle().Subject;
        organizer.Id.Should().Be(result.Value);
        organizer.Status.Should().Be(OrganizerStatus.Pending);
        organizer.CreatedByMemberId.Should().Be(memberId);

        var organizerMember = _dbContext.OrganizerMemberData.Should().ContainSingle().Subject;
        organizerMember.OrganizerId.Should().Be(organizer.Id);
        organizerMember.MemberId.Should().Be(memberId);
        organizerMember.Role.Should().Be(OrganizerMemberRole.Owner);
    }

    // ORG-APPLY-003
    [Fact]
    public async Task HandleAsync_WithoutName_ReturnsValidationErrorAndCreatesNoOrganizer()
    {
        var result = await _handler.HandleAsync(Guid.NewGuid(), new ApplyForOrganizerRequest(""), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Validation);
        _dbContext.OrganizerData.Should().BeEmpty();
        _dbContext.OrganizerMemberData.Should().BeEmpty();
    }

    // ORG-APPLY-004
    [Fact]
    public async Task HandleAsync_WithNameOver100CharactersAfterTrim_ReturnsValidationErrorAndCreatesNoOrganizer()
    {
        var tooLongName = new string('a', 101);

        var result = await _handler.HandleAsync(Guid.NewGuid(), new ApplyForOrganizerRequest(tooLongName), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Validation);
        _dbContext.OrganizerData.Should().BeEmpty();
    }

    // ORG-APPLY-001 邊界值
    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    public async Task HandleAsync_WithNameAtTrimmedLengthBoundary_Succeeds(int length)
    {
        // 頭尾各加一個空白，驗證「去除頭尾空白後」剛好落在邊界值，而非原始字串長度。
        var name = " " + new string('a', length) + " ";

        var result = await _handler.HandleAsync(Guid.NewGuid(), new ApplyForOrganizerRequest(name), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _dbContext.OrganizerData.Should().ContainSingle().Which.Name.Should().Be(new string('a', length));
    }
}
