namespace ProjectC.Application.Organizers.SwitchOrganizerContext;

/// <summary>
/// 切換操作情境成功的回應。刻意不含 Refresh Token 明文——這是原地更新既有那一筆 Refresh Token
/// 記錄，MUST NOT 觸發輪替（見 organizer-management design.md 決策 1、spec.md ORG-SWITCH-007）。
/// </summary>
public sealed record SwitchOrganizerContextResultDto(string AccessToken);
