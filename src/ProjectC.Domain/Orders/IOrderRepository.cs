namespace ProjectC.Domain.Orders;

public interface IOrderRepository
{
    /// <summary>實作 MUST 一併載入 <see cref="Order.Items"/>；<c>ConfirmOrderHandler</c>/<c>CancelOrderHandler</c> 假設這個集合已完整。</summary>
    Task<Order?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>供 Admin 訂單列表/明細使用，實作 MUST 一併載入 <see cref="Order.Items"/>（跟 <see cref="GetByIdAsync"/> 一致）。</summary>
    Task<IReadOnlyList<Order>> GetAllAsync(CancellationToken cancellationToken);

    /// <summary>找出狀態為 Pending 且已超過到期時間的訂單 Id 清單，只回傳 Id、不加鎖，供背景清理掃描候選訂單使用。</summary>
    Task<IReadOnlyList<Guid>> GetExpiredPendingOrderIdsAsync(DateTime now, CancellationToken cancellationToken);

    /// <summary>
    /// 強制用資料庫目前的值覆寫這個追蹤中 <paramref name="order"/> 實體的純量欄位（<c>Status</c>/<c>HeldUntilUtc</c>），
    /// 不重新載入 <see cref="Order.Items"/>。<paramref name="order"/> MUST 是同一個 <c>DbContext</c> 內剛透過
    /// <see cref="GetByIdAsync"/> 查出、目前仍被追蹤的同一個實體實例（見 ticketing-purchase design.md 決策 3、決策 4）。
    /// </summary>
    Task ReloadAsync(Order order, CancellationToken cancellationToken);

    void Add(Order order);
}
