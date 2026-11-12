using Microsoft.Extensions.Options;
using ProjectC.Domain.Notifications;
using ProjectC.Domain.Payments;
using ProjectC.Infrastructure.Notifications;
using ProjectC.Infrastructure.Payments;

namespace ProjectC.WebApi.Extensions;

// 放在 WebApi 而非 Application：判斷環境需要 IHostEnvironment，且需引用 Infrastructure 的 Mock 型別
// （mock-service-production-guard design.md 決策 1、2）。依 DI 實際解析的型別判斷，不依賴額外的 Provider 設定。
public sealed class MockServicesOptionsValidator : IValidateOptions<MockServicesOptions>
{
    private readonly IHostEnvironment _environment;
    private readonly IPaymentGateway _paymentGateway;
    private readonly IEmailNotificationService _emailNotificationService;

    public MockServicesOptionsValidator(
        IHostEnvironment environment,
        IPaymentGateway paymentGateway,
        IEmailNotificationService emailNotificationService)
    {
        _environment = environment;
        _paymentGateway = paymentGateway;
        _emailNotificationService = emailNotificationService;
    }

    public static IReadOnlyList<string> FindMockServiceNames(
        IPaymentGateway paymentGateway,
        IEmailNotificationService emailNotificationService)
    {
        var names = new List<string>();
        if (paymentGateway is MockPaymentGateway)
        {
            names.Add("IPaymentGateway（付款）");
        }

        if (emailNotificationService is MockEmailNotificationService)
        {
            names.Add("IEmailNotificationService（Email）");
        }

        return names;
    }

    public ValidateOptionsResult Validate(string? name, MockServicesOptions options)
    {
        if (!_environment.IsProduction() || options.AllowInProduction)
        {
            return ValidateOptionsResult.Success;
        }

        var mockServiceNames = FindMockServiceNames(_paymentGateway, _emailNotificationService);
        if (mockServiceNames.Count == 0)
        {
            return ValidateOptionsResult.Success;
        }

        return ValidateOptionsResult.Fail(
            "Production 環境不得使用 Mock 實作；僅限展示／驗證用途時，須明確設定 Mocks:AllowInProduction=true。"
            + $"目前為 Mock 的服務：{string.Join("、", mockServiceNames)}。");
    }
}
