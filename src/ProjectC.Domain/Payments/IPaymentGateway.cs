namespace ProjectC.Domain.Payments;

public interface IPaymentGateway
{
    /// <summary>
    /// 對 <paramref name="orderId"/> 這筆訂單嘗試扣款 <paramref name="amount"/>。
    /// <paramref name="orderId"/> 目前只是傳給呼叫端用來識別這筆付款對應哪一筆訂單的值，
    /// MUST NOT 被當成已經解決的冪等鍵——即使拿它當冪等鍵用，保護範圍也僅止於「同一次請求的重複重送」，
    /// 不涵蓋「<see cref="PaymentResult.Declined"/> 後買家業務層級重試」的情況（單一 <paramref name="orderId"/>
    /// 無法區分這兩者）；真實金流串接時需要改用複合鍵（例如 <paramref name="orderId"/> + 嘗試次數/nonce），
    /// 不能直接照搬本次的單一 <paramref name="orderId"/> 設計（見 order-payment-gateway-alignment design.md 決策 7）。
    /// 介面沒有內建 timeout 概念，呼叫端透過 <paramref name="cancellationToken"/> 取消，不代表真實實作不需要自己的逾時策略。
    /// </summary>
    Task<PaymentResult> ChargeAsync(Guid orderId, decimal amount, CancellationToken cancellationToken);
}

/// <summary>
/// 只有 <see cref="Succeeded"/>/<see cref="Declined"/> 兩種值，沒有「未知/處理中」狀態——假設付款是同步、
/// 即時可知結果的，不支援真實金流常見的非同步 webhook 確認流程；若未來要支援，需要擴充第三種狀態並搭配訂單暫留機制。
/// </summary>
public enum PaymentResult
{
    Succeeded,
    Declined,
}
