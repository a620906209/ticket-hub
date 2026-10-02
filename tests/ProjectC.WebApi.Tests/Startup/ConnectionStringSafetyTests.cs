using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ProjectC.WebApi.Tests.TestSupport;

namespace ProjectC.WebApi.Tests.Startup;

/// <summary>
/// real-name-verification tasks.md 5.17（design.md 決策 5）：Npgsql 的 Include Error Detail 會把違反約束的欄位值
/// （例如實名、末四碼）寫進例外訊息，再經由全域例外處理進入日誌。這是任何環境都禁止的設定，
/// 以測試讓違反的設定變更在 CI 失敗，而不是靠人工記得。
/// </summary>
public class ConnectionStringSafetyTests : IClassFixture<CustomWebApplicationFactory>
{
    private const string ForbiddenKeyword = "includeerrordetail";

    private readonly CustomWebApplicationFactory _factory;

    public ConnectionStringSafetyTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private static string Normalize(string value)
        => new string(value.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant();

    private static DirectoryInfo FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ProjectC.slnx")))
        {
            directory = directory.Parent;
        }

        return directory ?? throw new InvalidOperationException($"找不到 ProjectC.slnx（起點：{AppContext.BaseDirectory}）。");
    }

    [Fact]
    public void RepositoryConfigurationFiles_DoNotEnableIncludeErrorDetail()
    {
        var root = FindRepositoryRoot();
        var excludedSegments = new[] { "bin", "obj", "node_modules", ".git" };
        var configurationFiles = root.EnumerateFiles("appsettings*.json", SearchOption.AllDirectories)
            .Concat(root.EnumerateFiles("docker-compose*.yml", SearchOption.TopDirectoryOnly))
            .Concat(root.EnumerateFiles(".env.example", SearchOption.TopDirectoryOnly))
            .Where(file => !file.FullName.Split(Path.DirectorySeparatorChar).Intersect(excludedSegments).Any())
            .ToList();

        configurationFiles.Select(f => f.Name).Should().Contain(["appsettings.json", "docker-compose.yml", ".env.example"],
            "確認真的掃描到設定檔，避免路徑錯誤讓這個檢查空轉通過");
        foreach (var file in configurationFiles)
        {
            Normalize(File.ReadAllText(file.FullName)).Should().NotContain(ForbiddenKeyword, $"{file.FullName} 不得啟用 Include Error Detail");
        }
    }

    [Fact]
    public void ResolvedConnectionStrings_DoNotEnableIncludeErrorDetail()
    {
        var connectionStrings = _factory.Services.GetRequiredService<IConfiguration>().GetSection("ConnectionStrings").GetChildren().ToList();

        connectionStrings.Should().Contain(c => c.Key == "DefaultConnection" && !string.IsNullOrEmpty(c.Value));
        foreach (var connectionString in connectionStrings)
        {
            Normalize(connectionString.Value ?? string.Empty).Should().NotContain(ForbiddenKeyword, $"ConnectionStrings:{connectionString.Key}");
        }
    }
}
