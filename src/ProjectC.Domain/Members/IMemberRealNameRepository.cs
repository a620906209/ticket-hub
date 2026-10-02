namespace ProjectC.Domain.Members;

/// <summary>
/// 會員實名的讀寫。刻意不經由 <c>IApplicationDbContext.Members</c>：一次性登記的並發保證需要條件式
/// <c>ExecuteUpdateAsync</c>，Application 測試的 MockQueryable 無法執行（real-name-verification design.md 決策 2）。
/// </summary>
public interface IMemberRealNameRepository
{
    /// <summary>只在該會員尚未登記（<c>RealName == null</c>）時一次寫入兩欄；回傳是否實際寫入。
    /// 被其他請求搶先登記或會員不存在時回傳 <see langword="false"/>。這個條件 MUST 與
    /// <see cref="Member.RegisterRealName"/> 的「只能登記一次」規則一致（不變量 I2：實名只增不減）。</summary>
    Task<bool> TryRegisterAsync(Guid memberId, string realName, string nationalIdLast4, CancellationToken cancellationToken);

    /// <summary>唯讀、不加鎖，只投影兩欄；未登記或會員不存在時回傳 <see langword="null"/>。</summary>
    Task<MemberRealName?> GetAsync(Guid memberId, CancellationToken cancellationToken);
}
