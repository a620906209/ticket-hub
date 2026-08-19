namespace ProjectC.Infrastructure.Payments;

public sealed class MockPaymentGatewayOptions
{
    public const string SectionName = "MockPaymentGateway";

    public bool AlwaysSucceed { get; set; } = true;
}
