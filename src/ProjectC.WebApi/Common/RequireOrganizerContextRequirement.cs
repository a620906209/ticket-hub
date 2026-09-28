using Microsoft.AspNetCore.Authorization;

namespace ProjectC.WebApi.Common;

/// <summary>
/// 標記型 Requirement：呼叫端 MUST 已切換至一個 Approved Organizer（Access Token 帶合法非空
/// OrganizerId claim）才能通過。實際驗證邏輯在 <see cref="RequireOrganizerContextHandler"/>。
/// </summary>
public sealed class RequireOrganizerContextRequirement : IAuthorizationRequirement;
