namespace ProjectC.Application.PurchaseQueue.JoinPurchaseQueue;

public sealed record JoinPurchaseQueueRequest(string CaptchaToken, string CaptchaAnswer);
