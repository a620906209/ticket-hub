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

    public async Task<IReadOnlyList<Order>> GetAllAsync(CancellationToken cancellationToken)
        => await _dbContext.Orders.Include(o => o.Items).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Guid>> GetExpiredPendingOrderIdsAsync(DateTime now, CancellationToken cancellationToken)
        => await _dbContext.Orders
            .Where(o => o.Status == OrderStatus.Pending && o.HeldUntilUtc <= now)
            .Select(o => o.Id)
            .ToListAsync(cancellationToken);

    public Task ReloadAsync(Order order, CancellationToken cancellationToken)
        => _dbContext.Entry(order).ReloadAsync(cancellationToken);

    public void Add(Order order) => _dbContext.Orders.Add(order);
}
