using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using ProjectC.Domain.Tickets;
using ProjectC.Infrastructure.Persistence;
using ProjectC.Infrastructure.Persistence.Repositories;
using ProjectC.Infrastructure.Tests.TestSupport;

namespace ProjectC.Infrastructure.Tests.Tickets;

/// <summary>
/// 票種批次存在性查詢（place-order-request-limits design.md 決策 4、tasks.md 3.2）。
/// no-tracking 是正確性要求而非效能選項：交易內的 GetForUpdateAsync 若遇到已追蹤的同 Id 實體，
/// EF identity resolution 會回傳鎖前快照，Fake repository 測不到這點，只能在真實 DbContext 上驗證。
/// </summary>
[Collection(PostgresCollection.Name)]
public class TicketTypeRepositoryTests
{
    private readonly PostgresFixture _fixture;

    public TicketTypeRepositoryTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task GetByIdsAsync_WhenTicketTypesExist_ReturnsUntrackedEntities()
    {
        var ticketTypeIds = await SeedCountBasedTicketTypesAsync(count: 2);

        await using var dbContext = _fixture.CreateDbContext();
        var ticketTypes = await new TicketTypeRepository(dbContext).GetByIdsAsync(ticketTypeIds, CancellationToken.None);

        ticketTypes.Select(t => t.Id).Should().BeEquivalentTo(ticketTypeIds);
        dbContext.ChangeTracker.Entries<TicketType>().Should().BeEmpty();
    }

    [Fact]
    public async Task GetByIdsAsync_WhenSomeIdsMissingOrUnrequested_ReturnsOnlyExistingRequestedTicketTypes()
    {
        var ticketTypeIds = await SeedCountBasedTicketTypesAsync(count: 3);
        var missingId = Guid.NewGuid();

        await using var dbContext = _fixture.CreateDbContext();
        var ticketTypes = await new TicketTypeRepository(dbContext).GetByIdsAsync([ticketTypeIds[0], missingId, ticketTypeIds[2]], CancellationToken.None);

        ticketTypes.Select(t => t.Id).Should().BeEquivalentTo([ticketTypeIds[0], ticketTypeIds[2]]);
    }

    [Fact]
    public async Task GetByIdsAsync_WithEmptyList_ReturnsEmptyWithoutQuery()
    {
        var counter = new CommandCounter();
        await using var dbContext = CreateDbContext(counter);

        var ticketTypes = await new TicketTypeRepository(dbContext).GetByIdsAsync([], CancellationToken.None);

        ticketTypes.Should().BeEmpty();
        counter.Count.Should().Be(0);

        // 正向對照：同一個 counter 在非空清單時必須計到查詢，證明 0 不是因為 interceptor 沒掛上。
        await new TicketTypeRepository(dbContext).GetByIdsAsync([Guid.NewGuid()], CancellationToken.None);
        counter.Count.Should().Be(1);
    }

    private async Task<List<Guid>> SeedCountBasedTicketTypesAsync(int count)
    {
        await using var dbContext = _fixture.CreateDbContext();
        var (eventId, _) = await TicketingTestData.SeedEventWithSeatsAsync(dbContext, seatCount: 0);
        var ticketTypeIds = new List<Guid>();
        for (var i = 0; i < count; i++)
            ticketTypeIds.Add(await TicketingTestData.SeedCountBasedTicketTypeAsync(dbContext, eventId));
        return ticketTypeIds;
    }

    private ApplicationDbContext CreateDbContext(CommandCounter counter)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .AddInterceptors(counter)
            .Options;
        return new ApplicationDbContext(options);
    }

    private sealed class CommandCounter : DbCommandInterceptor
    {
        private int _count;

        public int Count => _count;

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Interlocked.Increment(ref _count);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            return ValueTask.FromResult(result);
        }
    }
}
