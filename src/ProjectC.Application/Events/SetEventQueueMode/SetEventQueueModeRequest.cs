namespace ProjectC.Application.Events.SetEventQueueMode;

// Enabled MUST 為 bool?（nullable），不得用 bool：{} 缺漏欄位須繫結為 null 才能與明確指定 false 區分，
// 否則 FluentValidation 無法攔截「完全缺漏」（見 rate-limiting-queue design.md 決策 6）。
public sealed record SetEventQueueModeRequest(bool? Enabled);
