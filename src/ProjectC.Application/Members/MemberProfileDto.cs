using ProjectC.Domain.Members;

namespace ProjectC.Application.Members;

public sealed record MemberProfileDto(
    Guid Id,
    string Email,
    string DisplayName,
    string Role,
    bool IsActive,
    bool HasRegisteredRealName,
    string? RealName,
    string? NationalIdLast4Masked)
{
    /// <summary>查詢、更新個人資料與實名登記三處共用，確保末四碼一律以遮蔽值回傳。</summary>
    public static MemberProfileDto FromMember(Member member) => new(
        member.Id,
        member.Email,
        member.DisplayName,
        member.Role.ToString(),
        member.IsActive,
        member.HasRegisteredRealName,
        member.RealName,
        member.NationalIdLast4 is null ? null : RealNameMasking.MaskNationalIdLast4(member.NationalIdLast4));
}
