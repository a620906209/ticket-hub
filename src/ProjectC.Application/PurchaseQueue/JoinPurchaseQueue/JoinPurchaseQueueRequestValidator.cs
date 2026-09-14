using FluentValidation;
using ProjectC.Application.Common.Validation;

namespace ProjectC.Application.PurchaseQueue.JoinPurchaseQueue;

public sealed class JoinPurchaseQueueRequestValidator : AbstractValidator<JoinPurchaseQueueRequest>
{
    public JoinPurchaseQueueRequestValidator()
    {
        this.ApplyCaptchaValidation(x => x.CaptchaToken, x => x.CaptchaAnswer);
    }
}
