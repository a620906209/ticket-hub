using ProjectC.Domain.Payments;

namespace ProjectC.Infrastructure.Tests.TestSupport;

/// <summary>用 Interlocked 計數，供並發整合測試共用同一個 instance 統計實際觸發次數
/// （既有 Application.Tests 的 FakePaymentGateway 不是 thread-safe，且測試專案間不互相引用）。</summary>
public sealed class ThreadSafeFakePaymentGateway : IPaymentGateway
{
    private int _callCount;

    public int CallCount => _callCount;

    public Task<PaymentResult> ChargeAsync(Guid orderId, decimal amount, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _callCount);
        return Task.FromResult(PaymentResult.Succeeded);
    }
}
