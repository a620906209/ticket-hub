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

    public Task ReloadAsync(Order order, CancellationToken cancellationToken)
        => _dbContext.Entry(order).ReloadAsync(cancellationToken);

    public void Add(Order order) => _dbContext.Orders.Add(order);
}
