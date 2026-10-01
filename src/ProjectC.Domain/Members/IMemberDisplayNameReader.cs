namespace ProjectC.Domain.Members;

/// <summary>
/// 批次取得會員顯示名稱，供後台訂單列表辨識買家。刻意定義成介面而非讓 Handler 直接查
/// <c>IApplicationDbContext.Members</c>：「買家會員查不到」需要以 decorator 注入損毀做整合測試，
/// <c>DbSet</c> 無法包裝（order-display-enrichment design.md 決策 1）。
/// </summary>
public interface IMemberDisplayNameReader
{
    /// <summary>唯讀單次查詢，只投影 Id 與顯示名稱、不讀取 Email 等其他欄位；查不到的 Id 不出現在結果中，
    /// 比對缺失是呼叫端的責任。<paramref name="memberIds"/> 為空清單時 MUST 直接回傳空結果，不得執行任何資料庫查詢。</summary>
    Task<IReadOnlyDictionary<Guid, string>> GetDisplayNamesByIdsAsync(IReadOnlyList<Guid> memberIds, CancellationToken cancellationToken);
}
