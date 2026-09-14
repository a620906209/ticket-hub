namespace ProjectC.Application.Common.Interfaces;

// 純技術性、不承載業務語意的外部服務介面，比照 IDistributedLock／IDateTimeProvider／IQueryCache
// 既有慣例放置於 Application/Common/Interfaces，不放 Domain（見 captcha-verification design.md 決策 12）。
public interface ICaptchaService
{
    Task<CaptchaChallenge> GenerateAsync(CancellationToken cancellationToken);

    Task<bool> VerifyAsync(string token, string answer, CancellationToken cancellationToken);
}
