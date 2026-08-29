using FluentAssertions;
using ProjectC.Domain.PurchaseQueue;
using ProjectC.Infrastructure.Persistence.Repositories;
using ProjectC.Infrastructure.Tests.TestSupport;

namespace ProjectC.Infrastructure.Tests.PurchaseQueue;

// 比照既有 GetForUpdateAsyncTests 的 WithoutActiveTransaction 慣例（審查後新增，防禦性修正）：
// AddOrGetExistingAsync 依賴呼叫端與任何先前的 Expire() 等變更位於同一交易內，才能保證 5.1a 的
// SaveChangesAsync flush 有意義；沒有進行中的交易時 MUST fail fast，而不是靜默用各自獨立的 implicit
// transaction 執行、失去原子性保證卻不被任何人發現。
[Collection(PostgresCollection.Name)]
public class AddOrGetExistingAsyncTests
{
    private readonly PostgresFixture _fixture;

    public AddOrGetExistingAsyncTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task AddOrGetExistingAsync_WithoutActiveTransaction_ThrowsInvalidOperationException()
    {
        await using var dbContext = _fixture.CreateDbContext();
        var repository = new PurchaseQueueRepository(dbContext);
        var newEntry = new PurchaseQueueEntry(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTime.UtcNow);

        var act = () => repository.AddOrGetExistingAsync(newEntry, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
