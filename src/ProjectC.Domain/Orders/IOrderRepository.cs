namespace ProjectC.Domain.Orders;

public interface IOrderRepository
{
    /// <summary>實作 MUST 一併載入 <see cref="Order.Items"/>；<c>ConfirmOrderHandler</c>/<c>CancelOrderHandler</c> 假設這個集合已完整。</summary>
    Task<Order?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>只回傳所屬活動屬於指定 Organizer 的訂單，供後台訂單列表使用。過濾 MUST 在資料庫端執行（join <c>Events</c>），
    /// 不得把其他 Organizer 的訂單載入記憶體再過濾；實作 MUST 一併載入 <see cref="Order.Items"/>
    /// （見 order-report-redemption-organizer-scoping design.md Decision 1）。</summary>
    Task<IReadOnlyList<Order>> GetByOrganizerIdAsync(Guid organizerId, CancellationToken cancellationToken);

    /// <summary>以單一查詢（<c>OrderItems JOIN Orders JOIN Events</c>）只投影回該訂單明細所屬活動的 <c>OrganizerId</c>、
    /// <c>IsRealNameRequired</c> 與訂單的 <c>BuyerId</c>，查無時回傳 <see langword="null"/>。供核銷在持鎖交易內核對歸屬，
    /// 刻意不載入 <see cref="Order"/> 實體以縮短鎖持有時間（見 order-report-redemption-organizer-scoping design.md Decision 1、
    /// real-name-verification design.md 決策 4）。</summary>
    Task<RedemptionContext?> GetRedemptionContextByOrderItemIdAsync(Guid orderItemId, CancellationToken cancellationToken);

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
