using FluentAssertions;
using ProjectC.Application.Orders.GetOrders;
using ProjectC.Application.Tests.TestSupport;
using ProjectC.Domain.Members;
using ProjectC.Domain.Orders;

namespace ProjectC.Application.Tests.Orders.GetOrders;

public class GetOrdersHandlerTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
    private static readonly Guid OrganizerId = Guid.NewGuid();

    private readonly FakeOrderRepository _orderRepository = new();
    private readonly FakeMemberDisplayNameReader _memberDisplayNameReader = new();

    private Order SeedOrder(Guid organizerId, DateTime heldUntilUtc, Guid? buyerId = null, string? buyerDisplayName = null)
    {
        var eventId = Guid.NewGuid();
        _orderRepository.OrganizerIdByEventId[eventId] = organizerId;
        var resolvedBuyerId = buyerId ?? Guid.NewGuid();
        _memberDisplayNameReader.DisplayNamesByMemberId[resolvedBuyerId] = buyerDisplayName ?? $"Buyer {resolvedBuyerId:N}";
        var order = new Order(Guid.NewGuid(), eventId, resolvedBuyerId, heldUntilUtc, [new OrderItem(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, 500m)]);
        _orderRepository.Data.Add(order);
        return order;
    }

    private GetOrdersHandler CreateHandler(IMemberDisplayNameReader? memberDisplayNameReader = null)
        => new(_orderRepository, memberDisplayNameReader ?? _memberDisplayNameReader, new FakeDateTimeProvider { UtcNow = Now });

    // [ORD-LIST-001]
    [Fact]
    public async Task HandleAsync_ReturnsCallerOrganizerOrdersWithLiveStatus()
    {
        var pendingOrder = SeedOrder(OrganizerId, Now.AddMinutes(10), buyerDisplayName: "Alice");
        var expiredOrder = SeedOrder(OrganizerId, Now.AddMinutes(-1), buyerDisplayName: "Bob");
        var otherOrganizerOrder = SeedOrder(Guid.NewGuid(), Now.AddMinutes(10));

        var result = await CreateHandler().HandleAsync(OrganizerId, CancellationToken.None);

        result.Should().HaveCount(2);
        result.Should().NotContain(o => o.Id == otherOrganizerOrder.Id);
        result.Should().ContainSingle(o => o.Id == pendingOrder.Id && o.Status == "Pending" && o.BuyerDisplayName == "Alice");
        // 已逾時但持久化狀態仍是 Pending 的訂單，即時狀態 MUST 回報 Expired，不是持久化欄位本身。
        result.Should().ContainSingle(o => o.Id == expiredOrder.Id && o.Status == "Expired" && o.BuyerDisplayName == "Bob");
    }

    [Fact]
    public async Task HandleAsync_WhenNoOrders_ReturnsEmptyList()
    {
        var result = await CreateHandler().HandleAsync(OrganizerId, CancellationToken.None);

        result.Should().BeEmpty();
        // 比照 GetMyOrdersHandler：0 筆訂單時不發出買家名稱查詢（repository 的「空清單不查詢」只是額外防線）。
        _memberDisplayNameReader.GetDisplayNamesByIdsCallCount.Should().Be(0);
    }

    // [ORD-LIST-002] 單次批次查詢不重複買家；DTO 本身沒有 Email 欄位，回應不含 Email 由整合測試驗證序列化結果。
    [Fact]
    public async Task HandleAsync_WithTwoBuyers_ReturnsBuyerDisplayNamesWithSingleDistinctBatchQuery()
    {
        var aliceId = Guid.NewGuid();
        var bobId = Guid.NewGuid();
        var aliceFirstOrder = SeedOrder(OrganizerId, Now.AddMinutes(10), aliceId, "Alice");
        var aliceSecondOrder = SeedOrder(OrganizerId, Now.AddMinutes(10), aliceId, "Alice");
        var bobOrder = SeedOrder(OrganizerId, Now.AddMinutes(10), bobId, "Bob");

        var result = await CreateHandler().HandleAsync(OrganizerId, CancellationToken.None);

        result.Single(o => o.Id == aliceFirstOrder.Id).BuyerDisplayName.Should().Be("Alice");
        result.Single(o => o.Id == aliceSecondOrder.Id).BuyerDisplayName.Should().Be("Alice");
        result.Single(o => o.Id == bobOrder.Id).BuyerDisplayName.Should().Be("Bob");
        _memberDisplayNameReader.GetDisplayNamesByIdsCallCount.Should().Be(1);
        _memberDisplayNameReader.LastGetDisplayNamesByIdsIds.Should().BeEquivalentTo([aliceId, bobId]);
    }

    // [ORD-LIST-003] 與 GetAdminEventsHandler 對可為 null 的 CreatedByMemberId 回 null 不同：BuyerId 查不到即資料損毀。
    [Fact]
    public async Task HandleAsync_WhenBuyerMissing_ThrowsWithOrderAndBuyerIds()
    {
        SeedOrder(OrganizerId, Now.AddMinutes(10));
        var orphanOrder = SeedOrder(OrganizerId, Now.AddMinutes(10));
        _memberDisplayNameReader.DisplayNamesByMemberId.Remove(orphanOrder.BuyerId);

        var act = () => CreateHandler().HandleAsync(OrganizerId, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<InvalidOperationException>();
        exception.Which.Message.Should().Contain(orphanOrder.Id.ToString()).And.Contain(orphanOrder.BuyerId.ToString());
    }

    // 回傳筆數與需求相同、但缺的是其中一個 Id 時仍要判定缺失，不以筆數比較（design.md 決策 4）。
    [Fact]
    public async Task HandleAsync_WhenReaderReturnsSameCountButWrongId_ThrowsWithMissingBuyerId()
    {
        SeedOrder(OrganizerId, Now.AddMinutes(10));
        var secondOrder = SeedOrder(OrganizerId, Now.AddMinutes(10));
        var reader = new SubstitutingMemberDisplayNameReader(_memberDisplayNameReader, secondOrder.BuyerId, Guid.NewGuid());

        var act = () => CreateHandler(reader).HandleAsync(OrganizerId, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<InvalidOperationException>();
        exception.Which.Message.Should().Contain(secondOrder.BuyerId.ToString());
    }

    [Fact]
    public async Task HandleAsync_WithCallerToken_ForwardsTokenToEveryQuery()
    {
        SeedOrder(OrganizerId, Now.AddMinutes(10));
        using var cancellationTokenSource = new CancellationTokenSource();
        var token = cancellationTokenSource.Token;

        await CreateHandler().HandleAsync(OrganizerId, token);

        _orderRepository.LastGetByOrganizerIdToken.Should().Be(token);
        _memberDisplayNameReader.LastGetDisplayNamesByIdsToken.Should().Be(token);
    }

    [Fact]
    public async Task HandleAsync_WhenReaderIsCancelled_PropagatesOperationCanceledException()
    {
        SeedOrder(OrganizerId, Now.AddMinutes(10));

        var act = () => CreateHandler(new CancellingMemberDisplayNameReader()).HandleAsync(OrganizerId, CancellationToken.None);

        await act.Should().ThrowExactlyAsync<OperationCanceledException>();
    }

    private sealed class SubstitutingMemberDisplayNameReader(IMemberDisplayNameReader inner, Guid removedMemberId, Guid extraMemberId) : IMemberDisplayNameReader
    {
        public async Task<IReadOnlyDictionary<Guid, string>> GetDisplayNamesByIdsAsync(IReadOnlyList<Guid> memberIds, CancellationToken cancellationToken)
        {
            var displayNames = (await inner.GetDisplayNamesByIdsAsync(memberIds, cancellationToken))
                .Where(pair => pair.Key != removedMemberId)
                .ToDictionary(pair => pair.Key, pair => pair.Value);
            displayNames[extraMemberId] = "Unrelated";
            return displayNames;
        }
    }

    private sealed class CancellingMemberDisplayNameReader : IMemberDisplayNameReader
    {
        public Task<IReadOnlyDictionary<Guid, string>> GetDisplayNamesByIdsAsync(IReadOnlyList<Guid> memberIds, CancellationToken cancellationToken)
            => throw new OperationCanceledException();
    }
}
