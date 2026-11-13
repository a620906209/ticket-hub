using System.ComponentModel.DataAnnotations;

namespace ProjectC.Application.Common;

// 資料庫命令逾時、連線逾時與連線池上限（db-timeout-pool-tuning design.md 決策 1）。預設值等同 Npgsql 既有預設，
// 缺漏時行為不變，不需要 ValidateOnStart；0 在 Npgsql 代表無限等待，必須由驗證擋下。
public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    [Range(1, 3600)]
    public int CommandTimeoutSeconds { get; set; } = 30;

    [Range(1, 300)]
    public int ConnectionTimeoutSeconds { get; set; } = 15;

    [Range(1, 1000)]
    public int MaxPoolSize { get; set; } = 100;
}
