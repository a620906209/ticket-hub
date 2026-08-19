using ProjectC.Domain.Payments;

namespace ProjectC.Application.Tests.TestSupport;

public sealed class FakePaymentGateway : IPaymentGateway
{
    private readonly PaymentResult _result;

    public FakePaymentGateway(PaymentResult result)
    {
        _result = result;
    }

    public int CallCount { get; private set; }

    public Guid LastOrderId { get; private set; }

    public decimal LastAmount { get; private set; }

    public Task<PaymentResult> ChargeAsync(Guid orderId, decimal amount, CancellationToken cancellationToken)
    {
        CallCount++;
        LastOrderId = orderId;
        LastAmount = amount;
        return Task.FromResult(_result);
    }
}
