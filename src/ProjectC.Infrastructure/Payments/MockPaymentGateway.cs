using ProjectC.Domain.Payments;

namespace ProjectC.Infrastructure.Payments;

public sealed class MockPaymentGateway : IPaymentGateway
{
    private readonly MockPaymentGatewayOptions _options;

    public MockPaymentGateway(MockPaymentGatewayOptions options)
    {
        _options = options;
    }

    public Task<PaymentResult> ChargeAsync(Guid orderId, decimal amount, CancellationToken cancellationToken)
        => Task.FromResult(_options.AlwaysSucceed ? PaymentResult.Succeeded : PaymentResult.Declined);
}
