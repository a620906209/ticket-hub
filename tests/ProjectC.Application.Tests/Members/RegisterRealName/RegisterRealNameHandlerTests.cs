using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ProjectC.Application.Common;
using ProjectC.Application.Members.RegisterRealName;
using ProjectC.Application.Tests.TestSupport;
using ProjectC.Domain.Members;

namespace ProjectC.Application.Tests.Members.RegisterRealName;

/// <summary>實名一經登記不可修改，是「防止改實名繞過實名轉賣」的基礎（real-name-verification RNV-REGISTER-*）。</summary>
public class RegisterRealNameHandlerTests
{
    private readonly FakeApplicationDbContext _dbContext = new();
    private readonly FakeMemberRealNameRepository _memberRealNameRepository = new();
    private readonly RegisterRealNameHandler _handler;

    public RegisterRealNameHandlerTests()
    {
        _handler = new RegisterRealNameHandler(
            _dbContext, _memberRealNameRepository, new RegisterRealNameRequestValidator(), NullLogger<RegisterRealNameHandler>.Instance);
    }

    private Member SeedMember(bool isRealNameRegistered = false)
    {
        var member = Member.Register($"member-{Guid.NewGuid():N}@example.com", "Member", "hash");
        if (isRealNameRegistered)
        {
            member.RegisterRealName("王小明", "1234");
            _memberRealNameRepository.Data[member.Id] = new MemberRealName("王小明", "1234");
        }

        _dbContext.MemberData.Add(member);
        return member;
    }

    // RNV-REGISTER-001
    [Fact]
    public async Task HandleAsync_WhenNotRegistered_ReturnsMaskedProfileWithoutFullLast4()
    {
        var member = SeedMember();

        var result = await _handler.HandleAsync(member.Id, new RegisterRealNameRequest("王小明", "1234"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.HasRegisteredRealName.Should().BeTrue();
        result.Value.RealName.Should().Be("王小明");
        result.Value.NationalIdLast4Masked.Should().Be("**34");
        new[] { result.Value.Email, result.Value.DisplayName, result.Value.Role, result.Value.RealName, result.Value.NationalIdLast4Masked }
            .Should().OnlyContain(value => value == null || !value.Contains("1234"));
        _memberRealNameRepository.Data[member.Id].Should().Be(new MemberRealName("王小明", "1234"));
    }

    // RNV-REGISTER-002／RNV-REGISTER-003：送出相同值也不視為成功，否則客戶端會誤以為可以重送覆寫。
    [Theory]
    [InlineData("李大華", "5678")]
    [InlineData("王小明", "1234")]
    public async Task HandleAsync_WhenAlreadyRegistered_ReturnsConflictWithoutWriting(string realName, string nationalIdLast4)
    {
        var member = SeedMember(isRealNameRegistered: true);

        var result = await _handler.HandleAsync(member.Id, new RegisterRealNameRequest(realName, nationalIdLast4), CancellationToken.None);

        result.Error!.Type.Should().Be(ErrorType.Conflict);
        result.Error.Message.Should().Contain(member.Id.ToString());
        HexIdentifierText.RemoveHexIdentifiers(result.Error.Message).Should().NotContain(realName).And.NotContain(nationalIdLast4);
        _memberRealNameRepository.TryRegisterCallCount.Should().Be(0);
        _memberRealNameRepository.Data[member.Id].Should().Be(new MemberRealName("王小明", "1234"));
    }

    // RNV-REGISTER-004（Application 層）：讀取時未登記、條件式 UPDATE 卻沒命中＝被並發請求搶先。
    [Fact]
    public async Task HandleAsync_WhenConditionalUpdateLosesRace_ReturnsConflict()
    {
        var member = SeedMember();
        _memberRealNameRepository.TryRegisterResult = false;

        var result = await _handler.HandleAsync(member.Id, new RegisterRealNameRequest("王小明", "1234"), CancellationToken.None);

        result.Error!.Type.Should().Be(ErrorType.Conflict);
        HexIdentifierText.RemoveHexIdentifiers(result.Error.Message).Should().NotContain("王小明").And.NotContain("1234");
        _memberRealNameRepository.TryRegisterCallCount.Should().Be(1);
    }

    // RNV-REGISTER-006
    [Fact]
    public async Task HandleAsync_WhenMemberNotFound_ReturnsNotFoundWithoutWriting()
    {
        var memberId = Guid.NewGuid();

        var result = await _handler.HandleAsync(memberId, new RegisterRealNameRequest("王小明", "1234"), CancellationToken.None);

        result.Error!.Type.Should().Be(ErrorType.NotFound);
        result.Error.Message.Should().Contain(memberId.ToString());
        _memberRealNameRepository.TryRegisterCallCount.Should().Be(0);
    }

    // RNV-FORMAT-003：儲存值是 trim 後的結果，否則現場比對證件時會因空白不一致。
    [Fact]
    public async Task HandleAsync_WhenRealNameHasSurroundingWhitespace_StoresAndReturnsTrimmedValue()
    {
        var member = SeedMember();

        var result = await _handler.HandleAsync(member.Id, new RegisterRealNameRequest("  王小明  ", "1234"), CancellationToken.None);

        result.Value!.RealName.Should().Be("王小明");
        _memberRealNameRepository.Data[member.Id].RealName.Should().Be("王小明");
    }

    [Fact]
    public async Task HandleAsync_WhenFormatInvalid_ReturnsValidationWithoutWriting()
    {
        var member = SeedMember();

        var result = await _handler.HandleAsync(member.Id, new RegisterRealNameRequest("王小明", "98x7"), CancellationToken.None);

        result.Error!.Type.Should().Be(ErrorType.Validation);
        _memberRealNameRepository.TryRegisterCallCount.Should().Be(0);
    }

    [Fact]
    public async Task HandleAsync_ForwardsCancellationTokenToRepository()
    {
        var member = SeedMember();
        using var cancellationTokenSource = new CancellationTokenSource();

        await _handler.HandleAsync(member.Id, new RegisterRealNameRequest("王小明", "1234"), cancellationTokenSource.Token);

        _memberRealNameRepository.LastTryRegisterToken.Should().Be(cancellationTokenSource.Token);
    }

    [Fact]
    public async Task HandleAsync_WhenTokenAlreadyCancelled_ThrowsOperationCanceledExceptionWithoutWriting()
    {
        var member = SeedMember();
        using var cancellationTokenSource = new CancellationTokenSource();
        await cancellationTokenSource.CancelAsync();

        var act = () => _handler.HandleAsync(member.Id, new RegisterRealNameRequest("王小明", "1234"), cancellationTokenSource.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        _memberRealNameRepository.TryRegisterCallCount.Should().Be(0);
    }
}
