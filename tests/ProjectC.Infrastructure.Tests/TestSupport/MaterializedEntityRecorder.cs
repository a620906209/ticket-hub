using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore.Diagnostics;
using ProjectC.Domain.Orders;

namespace ProjectC.Infrastructure.Tests.TestSupport;

/// <summary>記錄 EF Core 實際具現化（materialize）了哪些 <see cref="Order"/>／<see cref="OrderItem"/>，
/// 用來直接驗證「其他租戶的資料沒有被載入記憶體」，不依賴解析 SQL 文字
/// （見 order-report-redemption-organizer-scoping tasks.md 0.3b）。</summary>
public sealed class MaterializedEntityRecorder : IMaterializationInterceptor
{
    public ConcurrentBag<Order> Orders { get; } = new();
    public ConcurrentBag<OrderItem> OrderItems { get; } = new();

    public object InitializedInstance(MaterializationInterceptionData materializationData, object entity)
    {
        switch (entity)
        {
            case Order order:
                Orders.Add(order);
                break;
            case OrderItem orderItem:
                OrderItems.Add(orderItem);
                break;
        }

        return entity;
    }
}
