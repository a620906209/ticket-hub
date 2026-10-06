using System.Text.Json;
using FluentAssertions;

namespace ProjectC.WebApi.Tests.Observability;

/// <summary>
/// order-placement-p95-optimization design.md 決策 1：量測時以環境變數把 OrderService 降到 Debug，分段耗時 log
/// 只能進 Seq、不得進 Console。這個限制寫在 appsettings.json 而不依賴量測時的覆寫；以測試鎖住，避免日後被刪掉
/// 而只在量測時才發現（量測檢查腳本會把 Console 出現分段 log 判為無效）。
/// </summary>
public class ConsoleSinkMinimumLevelTests
{
    [Fact]
    public void AppSettings_ConsoleSink_IsRestrictedToInformation()
    {
        var appSettingsPath = Path.Combine(FindRepositoryRoot().FullName, "src", "ProjectC.WebApi", "appsettings.json");
        using var document = JsonDocument.Parse(File.ReadAllText(appSettingsPath));

        var consoleSinks = document.RootElement.GetProperty("Serilog").GetProperty("WriteTo").EnumerateArray()
            .Where(sink => sink.GetProperty("Name").GetString() == "Console")
            .ToList();

        consoleSinks.Should().ContainSingle("確認真的找到 Console sink，避免設定結構改變讓這個檢查空轉通過");
        consoleSinks[0].GetProperty("Args").GetProperty("restrictedToMinimumLevel").GetString().Should().Be("Information");
    }

    private static DirectoryInfo FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ProjectC.slnx")))
        {
            directory = directory.Parent;
        }

        return directory ?? throw new InvalidOperationException("找不到 ProjectC.slnx，無法定位 repository 根目錄。");
    }
}
