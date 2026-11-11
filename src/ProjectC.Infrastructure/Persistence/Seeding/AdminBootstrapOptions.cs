namespace ProjectC.Infrastructure.Persistence.Seeding;

/// <summary>
/// 非 Development 環境建立首位 Admin 的設定（production-admin-bootstrap）。
/// 密碼一律由 Secret 注入；兩項皆未設定時 bootstrapper 略過，啟動期驗證見 WebApi 的 AdminBootstrapOptionsValidator。
/// </summary>
public class AdminBootstrapOptions
{
    public const string SectionName = "AdminBootstrap";

    public string Email { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public string DisplayName { get; set; } = "Administrator";
}
