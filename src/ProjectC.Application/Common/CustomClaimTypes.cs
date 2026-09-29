namespace ProjectC.Application.Common;

/// <summary>
/// 本專案自訂、非 BCL <see cref="System.Security.Claims.ClaimTypes"/> 內建的 JWT claim 型別字串。
/// Infrastructure（簽發）與 WebApi（讀取）兩端 MUST 共用同一個常數，不得各自寫死字串常值。
/// </summary>
public static class CustomClaimTypes
{
    public const string OrganizerId = "OrganizerId";
}
