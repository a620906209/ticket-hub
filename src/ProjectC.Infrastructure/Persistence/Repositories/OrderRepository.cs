using Microsoft.EntityFrameworkCore;
using ProjectC.Domain.Orders;

namespace ProjectC.Infrastructure.Persistence.Repositories;

public class OrderRepository : IOrderRepository
{
    private readonly ApplicationDbContext _dbContext;

    public OrderRepository(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<Order?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
        => _dbContext.Orders.Include(o => o.Items).FirstOrDefaultAsync(o => o.Id == id, cancellationToken);

    public async Task<IReadOnlyList<Order>> GetByOrganizerIdAsync(Guid organizerId, CancellationToken cancellationToken)
        => await _dbContext.Orders
            .Include(o => o.Items)
            .Where(o => _dbContext.Events.Any(e => e.Id == o.EventId && e.OrganizerId == organizerId))
            .ToListAsync(cancellationToken);

    public async Task<RedemptionContext?> GetRedemptionContextByOrderItemIdAsync(Guid orderItemId, CancellationToken cancellationToken)
        => await (
            from order in _dbContext.Orders
            from item in order.Items
            join @event in _dbContext.Events on order.EventId equals @event.Id
            where item.Id == orderItemId
            select new RedemptionContext(@event.OrganizerId, @event.IsRealNameRequired, order.BuyerId))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<Order>> GetByBuyerIdAsync(Guid buyerId, CancellationToken cancellationToken)
        => await _dbContext.Orders
            .AsNoTracking()
            .Include(o => o.Items)
            .Where(o => o.BuyerId == buyerId)
            .ToListAsync(cancellationToken);

    public Task<Order?> GetByOrderItemIdAsync(Guid orderItemId, CancellationToken cancellationToken)
        => _dbContext.Orders
            .AsNoTracking()
            .Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Items.Any(i => i.Id == orderItemId), cancellationToken);

    public async Task<IReadOnlyList<Guid>> GetExpiredPendingOrderIdsAsync(DateTime now, CancellationToken cancellationToken)
        => await _dbContext.Orders
            .Where(o => o.Status == OrderStatus.Pending && o.HeldUntilUtc <= now)
            .Select(o => o.Id)
            .ToListAsync(cancellationToken);

    public Task ReloadAsync(Order order, CancellationToken cancellationToken)
        => _dbContext.Entry(order).ReloadAsync(cancellationToken);

    public void Add(Order order) => _dbContext.Orders.Add(order);

    public async Task<IReadOnlyList<OrderItemSalesGroup>> GetPaidItemSalesByEventIdAsync(Guid eventId, CancellationToken cancellationToken)
        => await _dbContext.Orders
            .Where(o => o.EventId == eventId && o.Status == OrderStatus.Paid)
            .SelectMany(o => o.Items)
            .GroupBy(item => item.TicketTypeId)
            .Select(g => new OrderItemSalesGroup(
                g.Key,
                g.Count(),
                g.Sum(i => i.Quantity),
                g.Sum(i => i.Quantity * i.UnitPrice)))
            .ToListAsync(cancellationToken);
}
