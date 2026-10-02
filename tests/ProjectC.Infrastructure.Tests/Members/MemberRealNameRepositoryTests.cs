using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProjectC.Domain.Members;
using ProjectC.Infrastructure.Persistence;
using ProjectC.Infrastructure.Persistence.Repositories;
using ProjectC.Infrastructure.Tests.TestSupport;

namespace ProjectC.Infrastructure.Tests.Members;

/// <summary>real-name-verification design.md 決策 2：一次性登記的並發保證只能由真實資料庫的條件式 UPDATE 驗證，
/// Application 層的 Fake 無法涵蓋。</summary>
[Collection(PostgresCollection.Name)]
public class MemberRealNameRepositoryTests
{
    private readonly PostgresFixture _fixture;

    public MemberRealNameRepositoryTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<Guid> SeedMemberAsync()
    {
        await using var dbContext = _fixture.CreateDbContext();
        var member = Member.Register($"real-name-{Guid.NewGuid():N}@example.com", "Real Name Member", "hash");
        dbContext.Members.Add(member);
        await dbContext.SaveChangesAsync();
        return member.Id;
    }

    private async Task<(string? RealName, string? NationalIdLast4)> ReadRealNameColumnsAsync(Guid memberId)
    {
        await using var dbContext = _fixture.CreateDbContext();
        var member = await dbContext.Members.AsNoTracking().SingleAsync(m => m.Id == memberId);
        return (member.RealName, member.NationalIdLast4);
    }

    [Fact]
    public async Task TryRegisterAsync_WhenNotRegistered_WritesBothFieldsAndReturnsTrue()
    {
        var memberId = await SeedMemberAsync();
        await using var dbContext = _fixture.CreateDbContext();

        var isRegistered = await new MemberRealNameRepository(dbContext).TryRegisterAsync(memberId, "王小明", "1234", CancellationToken.None);

        isRegistered.Should().BeTrue();
        (await ReadRealNameColumnsAsync(memberId)).Should().Be(("王小明", "1234"));
    }

    // RNV-FORMAT-008：varchar(50) 以字元計：50 個擴充 B 區罕用字（UTF-16 共 100 個單位）必須能完整寫入，驗證器的 rune 計數才有意義。
    [Fact]
    public async Task TryRegisterAsync_WhenRealNameIs50SupplementaryPlaneCharacters_StoresFullName()
    {
        var realName = string.Concat(Enumerable.Repeat("\U00020000", 50));
        var memberId = await SeedMemberAsync();
        await using var dbContext = _fixture.CreateDbContext();

        var isRegistered = await new MemberRealNameRepository(dbContext).TryRegisterAsync(memberId, realName, "1234", CancellationToken.None);

        isRegistered.Should().BeTrue();
        (await ReadRealNameColumnsAsync(memberId)).Should().Be((realName, "1234"));
    }

    // 登記後不可變更：第二次寫入即使值不同也不得覆蓋（防止改實名繞過實名轉賣）。
    [Fact]
    public async Task TryRegisterAsync_WhenAlreadyRegistered_ReturnsFalseAndKeepsOriginalValues()
    {
        var memberId = await SeedMemberAsync();
        await using (var firstContext = _fixture.CreateDbContext())
            await new MemberRealNameRepository(firstContext).TryRegisterAsync(memberId, "王小明", "1234", CancellationToken.None);

        await using var secondContext = _fixture.CreateDbContext();
        var isRegistered = await new MemberRealNameRepository(secondContext).TryRegisterAsync(memberId, "李大華", "5678", CancellationToken.None);

        isRegistered.Should().BeFalse();
        (await ReadRealNameColumnsAsync(memberId)).Should().Be(("王小明", "1234"));
    }

    // RNV-REGISTER-004（資料庫層）：先讀後寫會讓兩個請求都成功、後寫者覆蓋前者；條件式 UPDATE 必須只讓一個命中，
    // 且兩欄不能一半來自 A、一半來自 B。
    [Fact]
    public async Task TryRegisterAsync_WhenConcurrentFirstRegistrations_ExactlyOneSucceedsAndValuesAreNotMixed()
    {
        var memberId = await SeedMemberAsync();
        await using var contextA = _fixture.CreateDbContext();
        await using var contextB = _fixture.CreateDbContext();

        var results = await Task.WhenAll(
            new MemberRealNameRepository(contextA).TryRegisterAsync(memberId, "王小明", "1111", CancellationToken.None),
            new MemberRealNameRepository(contextB).TryRegisterAsync(memberId, "李大華", "2222", CancellationToken.None));

        results.Should().ContainSingle(isRegistered => isRegistered);
        var expected = results[0] ? ("王小明", "1111") : ("李大華", "2222");
        (await ReadRealNameColumnsAsync(memberId)).Should().Be(expected);
    }

    [Fact]
    public async Task TryRegisterAsync_WhenMemberDoesNotExist_ReturnsFalse()
    {
        await using var dbContext = _fixture.CreateDbContext();

        var isRegistered = await new MemberRealNameRepository(dbContext).TryRegisterAsync(Guid.NewGuid(), "王小明", "1234", CancellationToken.None);

        isRegistered.Should().BeFalse();
    }

    [Fact]
    public async Task GetAsync_WhenNotRegistered_ReturnsNull()
    {
        var memberId = await SeedMemberAsync();
        await using var dbContext = _fixture.CreateDbContext();

        var realName = await new MemberRealNameRepository(dbContext).GetAsync(memberId, CancellationToken.None);

        realName.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_WhenRegistered_ReturnsBothFields()
    {
        var memberId = await SeedMemberAsync();
        await using var dbContext = _fixture.CreateDbContext();
        var repository = new MemberRealNameRepository(dbContext);
        await repository.TryRegisterAsync(memberId, "王小明", "1234", CancellationToken.None);

        var realName = await repository.GetAsync(memberId, CancellationToken.None);

        realName.Should().Be(new MemberRealName("王小明", "1234"));
    }

    [Fact]
    public async Task TryRegisterAsyncAndGetAsync_WhenTokenAlreadyCancelled_ThrowOperationCanceledException()
    {
        var memberId = await SeedMemberAsync();
        await using var dbContext = _fixture.CreateDbContext();
        var repository = new MemberRealNameRepository(dbContext);
        using var cancellationTokenSource = new CancellationTokenSource();
        await cancellationTokenSource.CancelAsync();

        await FluentActions.Awaiting(() => repository.TryRegisterAsync(memberId, "王小明", "1234", cancellationTokenSource.Token))
            .Should().ThrowAsync<OperationCanceledException>();
        await FluentActions.Awaiting(() => repository.GetAsync(memberId, cancellationTokenSource.Token))
            .Should().ThrowAsync<OperationCanceledException>();
        (await ReadRealNameColumnsAsync(memberId)).Should().Be(((string?)null, (string?)null));
    }

    // design.md 決策 1、5：check constraint 守住「兩欄同時有值」；違反時的例外訊息若帶出寫入值
    // （例如連線字串誤加 Include Error Detail），會讓個資經由全域例外 log 外洩。
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CheckConstraint_WhenOnlyOneColumnWritten_RejectsWriteWithoutLeakingValues(bool isRealNameOnly)
    {
        const string realName = "約束測試甲乙";
        const string nationalIdLast4 = "9753";
        var memberId = await SeedMemberAsync();
        await using var dbContext = _fixture.CreateDbContext();
        var query = dbContext.Members.Where(m => m.Id == memberId);

        Func<Task<int>> act = isRealNameOnly
            ? () => query.ExecuteUpdateAsync(setters => setters.SetProperty(m => m.RealName, realName))
            : () => query.ExecuteUpdateAsync(setters => setters.SetProperty(m => m.NationalIdLast4, nationalIdLast4));

        var exception = (await act.Should().ThrowAsync<Exception>()).Which;
        exception.Should().Match<Exception>(e => e is DbUpdateException || e is PostgresException);
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            HexIdentifierText.RemoveHexIdentifiers(current.Message).Should().NotContain(realName).And.NotContain(nationalIdLast4);
        }

        var postgresException = exception as PostgresException ?? exception.InnerException as PostgresException;
        postgresException!.ConstraintName.Should().Be("CK_Members_RealName_NationalIdLast4");
        (await ReadRealNameColumnsAsync(memberId)).Should().Be(((string?)null, (string?)null));
    }
}
