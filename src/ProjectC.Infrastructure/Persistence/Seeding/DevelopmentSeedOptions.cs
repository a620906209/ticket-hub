namespace ProjectC.Infrastructure.Persistence.Seeding;

/// <summary>
/// 僅供本機開發／Docker Compose 展示用的初始資料設定（event-management-organizer-scoping tasks.md 6.2）。
/// 帳密一律由 compose env_file（.env）注入，不寫死在程式碼或進版控的設定檔；未設定時 seeder 直接略過。
/// </summary>
public class DevelopmentSeedOptions
{
    public const string SectionName = "DevelopmentSeed";

    public string AdminEmail { get; set; } = string.Empty;

    public string AdminPassword { get; set; } = string.Empty;

    public string AdminDisplayName { get; set; } = "Demo Admin";

    public string OrganizerName { get; set; } = "Demo Organizer";
}
