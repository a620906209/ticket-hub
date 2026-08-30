namespace ProjectC.Domain.Orders;

public interface IOrderRepository
{
    /// <summary>實作 MUST 一併載入 <see cref="Order.Items"/>；<c>ConfirmOrderHandler</c>/<c>CancelOrderHandler</c> 假設這個集合已完整。</summary>
    Task<Order?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>供 Admin 訂單列表/明細使用，實作 MUST 一併載入 <see cref="Order.Items"/>（跟 <see cref="GetByIdAsync"/> 一致）。</summary>
    Task<IReadOnlyList<Order>> GetAllAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<Order>> GetByBuyerIdAsync(Guid buyerId, CancellationToken cancellationToken);

    Task<Order?> GetByOrderItemIdAsync(Guid orderItemId, CancellationToken cancellationToken);

    /// <summary>找出狀態為 Pending 且已超過到期時間的訂單 Id 清單，只回傳 Id、不加鎖，供背景清理掃描候選訂單使用。</summary>
    Task<IReadOnlyList<Guid>> GetExpiredPendingOrderIdsAsync(DateTime now, CancellationToken cancellationToken);

    /// <summary>
    /// 強制用資料庫目前的值覆寫這個追蹤中 <paramref name="order"/> 實體的純量欄位（<c>Status</c>/<c>HeldUntilUtc</c>），
    /// 不重新載入 <see cref="Order.Items"/>。<paramref name="order"/> MUST 是同一個 <c>DbContext</c> 內剛透過
    /// <see cref="GetByIdAsync"/> 查出、目前仍被追蹤的同一個實體實例（見 ticketing-purchase design.md 決策 3、決策 4）。
    /// </summary>
    Task ReloadAsync(Order order, CancellationToken cancellationToken);

    void Add(Order order);

    /// <summary>
    /// 供銷售報表（sales-report）使用，回傳指定活動下已付款訂單的項目，依 <c>TicketTypeId</c> 分組彙總。
    /// <list type="bullet">
    /// <item>只包含 <c>Order.EventId == eventId</c> 且 <c>Order.Status == OrderStatus.Paid</c> 的 <c>OrderItem</c>。</item>
    /// <item>依 <c>TicketTypeId</c> 分組，每個相異的 <c>TicketTypeId</c> 值最多出現一組。</item>
    /// <item><c>TicketTypeId = null</c> 的項目自成一組，最多一組（沒有這類項目時不會出現這一組）。</item>
    /// <item>沒有符合條件的項目時回傳空集合，MUST NOT 回傳 <see langword="null"/>。</item>
    /// <item><see cref="OrderItemSalesGroup.ItemCount"/> 是該分組內 <c>OrderItem</c> 的筆數，不是售出張數；
    /// <see cref="OrderItemSalesGroup.QuantitySold"/> 才是依 <c>Quantity</c> 加總的售出張數，兩者語意不同。</item>
    /// <item>這個方法不判斷 <c>TicketTypeId</c> 是否真的屬於 <paramref name="eventId"/> 對應的活動（只依 <c>TicketTypeId</c>
    /// 本身分組）——「是否屬於本活動」是呼叫端（Application 層）依票種目錄另外判斷的責任，見 sales-report design.md 決策 2、3。</item>
    /// </list>
    /// </summary>
    Task<IReadOnlyList<OrderItemSalesGroup>> GetPaidItemSalesByEventIdAsync(Guid eventId, CancellationToken cancellationToken);
}
