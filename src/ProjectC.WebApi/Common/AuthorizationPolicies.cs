namespace ProjectC.WebApi.Common;

public static class AuthorizationPolicies
{
    public const string AdminOnly = "AdminOnly";

    /// <summary>本次僅定義，不套用至任何既有 Controller；套用是依賴本能力的後續變更（例如
    /// event-management-organizer-scoping）的範圍，見 organizer-management tasks.md 4.3a。</summary>
    public const string RequireOrganizerContext = "RequireOrganizerContext";
}
