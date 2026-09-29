namespace ProjectC.WebApi.Common;

public static class AuthorizationPolicies
{
    public const string AdminOnly = "AdminOnly";

    /// <summary>要求 Access Token 帶合法 <c>OrganizerId</c> claim（已切換至一個 Organizer）；只驗 claim 格式、不即時查表。
    /// 套用於活動／場館（event-management-organizer-scoping）、訂單／核銷／銷售報表（order-report-redemption-organizer-scoping）與熱門搶購模式開關（purchase-queue-organizer-scoping）端點。</summary>
    public const string RequireOrganizerContext = "RequireOrganizerContext";
}
