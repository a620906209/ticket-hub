using System.Text.RegularExpressions;
using FluentAssertions;

namespace ProjectC.WebApi.Tests.Startup;

// mock-service-production-guard MOCKGUARD-009：以 Production 執行 Mock 的既有部署必須明示放行，
// 否則守門生效後這些部署會啟動失敗。以讀檔斷言鎖住，避免日後有人刪掉旗標才在部署時發現。
public class MockGuardDeploymentConfigTests
{
    private const string FlagKey = "Mocks__AllowInProduction";

    private static DirectoryInfo FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ProjectC.slnx")))
        {
            directory = directory.Parent;
        }

        return directory ?? throw new InvalidOperationException($"找不到 ProjectC.slnx（起點：{AppContext.BaseDirectory}）。");
    }

    private static string ReadRepositoryFile(string relativePath)
    {
        var path = Path.Combine(FindRepositoryRoot().FullName, relativePath);
        File.Exists(path).Should().BeTrue($"{relativePath} 必須存在");
        return File.ReadAllText(path);
    }

    private static void AssertFlagDeclaredExactlyOnceAsTrue(string relativePath)
    {
        var content = ReadRepositoryFile(relativePath);

        Regex.Matches(content, Regex.Escape(FlagKey)).Should().HaveCount(1, $"{relativePath} 的 {FlagKey} 鍵必須恰好出現一次");
        Regex.IsMatch(content, $@"(?m)^\s*{FlagKey}:\s*""?true""?\s*(#.*)?$")
            .Should().BeTrue($"{relativePath} 的 {FlagKey} 必須設為 true");
    }

    [Fact]
    public void K8sConfigMap_DeclaresMocksAllowInProductionTrue()
        => AssertFlagDeclaredExactlyOnceAsTrue("k8s/10-configmap.yaml");

    [Fact]
    public void VerifyCompose_DeclaresMocksAllowInProductionTrue()
        => AssertFlagDeclaredExactlyOnceAsTrue("deploy/docker-compose.verify.yml");

    // verify compose 未設 ASPNETCORE_ENVIRONMENT，靠映像預設值成為 Production；鎖住這個前提，
    // 日後若預設改變，上面的旗標需求與本守門的適用範圍都要重新檢視。
    [Fact]
    public void ApiDockerfile_DefaultsToProductionEnvironment()
        => ReadRepositoryFile("deploy/api.Dockerfile").Should().Contain("ASPNETCORE_ENVIRONMENT=Production");
}
