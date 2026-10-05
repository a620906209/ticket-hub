using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ProjectC.LoadTest.Seeder.Tests;

/// <summary>
/// k6-load-test spec「壓測 seeder 準備壓測帳號與 token 檔」：seeder 能簽出 api 接受的 token，
/// 所以只允許在本機 compose 的 Development 環境執行；任何一項檢查不符都必須在建立 DbContext 之前結束，
/// 確保不會寫入其他資料庫、也不會留下 token 檔。這裡以「DbContext 工廠沒被呼叫」觀察這個保證，不碰 DB。
/// </summary>
public sealed class SeederEntryPointTests : IDisposable
{
    private const string ValidSigningKey = "0123456789abcdef0123456789abcdef"; // 恰好 32 個字元

    private readonly string _tokenFilePath = Path.Combine(Path.GetTempPath(), $"seeder-entry-{Guid.NewGuid():N}", "tokens.json");
    private bool _isDbContextFactoryInvoked;

    public void Dispose()
    {
        var directory = Path.GetDirectoryName(_tokenFilePath)!;
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }

    private sealed class DbContextFactoryInvokedException : Exception;

    private static Dictionary<string, string?> ValidSettings() => new()
    {
        ["ASPNETCORE_ENVIRONMENT"] = "Development",
        ["ConnectionStrings:DefaultConnection"] = "Host=db;Port=5432;Database=projectc;Username=u;Password=p",
        ["Jwt:Issuer"] = "issuer",
        ["Jwt:Audience"] = "audience",
        ["Jwt:SigningKey"] = ValidSigningKey,
    };

    private Task<int> RunAsync(Dictionary<string, string?> settings, params string[] args)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var context = new SeederEntryPointContext(
            args,
            configuration,
            _ =>
            {
                _isDbContextFactoryInvoked = true;
                // 所有檢查都通過時才會走到這裡；以例外中止，避免單元測試真的連 DB。
                throw new DbContextFactoryInvokedException();
            },
            _tokenFilePath,
            TextWriter.Null,
            TextWriter.Null);
        return SeederEntryPoint.RunAsync(context, CancellationToken.None);
    }

    private async Task AssertRejectedAsync(Dictionary<string, string?> settings, params string[] args)
    {
        var exitCode = await RunAsync(settings, args);

        exitCode.Should().NotBe(0);
        _isDbContextFactoryInvoked.Should().BeFalse("檢查不通過時不得建立 DbContext（因此不寫 DB）");
        File.Exists(_tokenFilePath).Should().BeFalse("檢查不通過時不得寫 token 檔");
    }

    private async Task AssertPassesChecksAsync(Dictionary<string, string?> settings, params string[] args)
    {
        var act = () => RunAsync(settings, args);

        await act.Should().ThrowAsync<DbContextFactoryInvokedException>("全部檢查通過後才會建立 DbContext");
        _isDbContextFactoryInvoked.Should().BeTrue();
    }

    [Fact]
    public async Task RunAsync_WithValidEnvironmentAndNoArguments_PassesChecks()
    {
        await AssertPassesChecksAsync(ValidSettings());
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("")]
    [InlineData(null)]
    public async Task RunAsync_LT_SEED_003_WhenEnvironmentIsNotDevelopment_RejectsBeforeCreatingDbContext(string? environment)
    {
        var settings = ValidSettings();
        settings["ASPNETCORE_ENVIRONMENT"] = environment;

        await AssertRejectedAsync(settings);
    }

    [Theory]
    [InlineData("Jwt:Issuer", "")]
    [InlineData("Jwt:Audience", "")]
    [InlineData("Jwt:SigningKey", "")]
    [InlineData("Jwt:Issuer", null)]
    [InlineData("Jwt:SigningKey", "0123456789abcdef0123456789abcde")] // 31 個字元
    public async Task RunAsync_LT_SEED_007_WhenJwtSettingsAreIncomplete_RejectsBeforeCreatingDbContext(string key, string? value)
    {
        var settings = ValidSettings();
        settings[key] = value;

        await AssertRejectedAsync(settings);
    }

    [Fact]
    public async Task RunAsync_LT_SEED_007_WhenSigningKeyIsExactly32Characters_PassesChecks()
    {
        ValidSigningKey.Should().HaveLength(32);

        await AssertPassesChecksAsync(ValidSettings());
    }

    [Theory]
    [InlineData("--buyers", "0")]
    [InlineData("--buyers", "-1")]
    [InlineData("--buyers", "1001")]
    [InlineData("--buyers", "abc")]
    [InlineData("--buyers", " 5")]
    [InlineData("--output", "x")]
    [InlineData("--buyers")]
    [InlineData("--buyers", "5", "--buyers", "6")]
    [InlineData("--buyers", "5", "--output", "x")]
    public async Task RunAsync_LT_SEED_005_WithInvalidArguments_ReturnsUsageErrorBeforeCreatingDbContext(params string[] args)
    {
        await AssertRejectedAsync(ValidSettings(), args);

        (await RunAsync(ValidSettings(), args)).Should().Be(SeederEntryPoint.InvalidArgumentsExitCode);
    }

    [Theory]
    [InlineData("Host=localhost;Port=5432;Database=projectc;Username=u;Password=p")]
    [InlineData("Host=127.0.0.1;Port=5432;Database=projectc;Username=u;Password=p")]
    [InlineData("Host=db.example.com;Port=5432;Database=projectc;Username=u;Password=p")]
    [InlineData("Host=db,other;Port=5432;Database=projectc;Username=u;Password=p")]
    [InlineData("not a connection string")]
    [InlineData("")]
    public async Task RunAsync_LT_SEED_005_WhenDatabaseHostIsNotComposeDb_RejectsBeforeCreatingDbContext(string connectionString)
    {
        var settings = ValidSettings();
        settings["ConnectionStrings:DefaultConnection"] = connectionString;

        await AssertRejectedAsync(settings);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("1000")]
    public async Task RunAsync_LT_SEED_005_WithBoundaryBuyerCount_PassesChecks(string buyers)
    {
        await AssertPassesChecksAsync(ValidSettings(), "--buyers", buyers);
    }

    [Fact]
    public void Serialize_LT_SEED_001_WritesTheJsonShapeK6Reads()
    {
        var issuedAtUtc = new DateTime(2026, 10, 5, 8, 0, 0, DateTimeKind.Utc);
        var tokenFile = new LoadTestTokenFile(issuedAtUtc, "admin-token", ["buyer-1", "buyer-2", "buyer-3"]);

        using var json = System.Text.Json.JsonDocument.Parse(tokenFile.Serialize());

        // k6 腳本依這三個 camelCase 欄位名讀檔，改名會讓壓測在 setup 失敗。
        var root = json.RootElement;
        root.GetProperty("issuedAtUtc").GetDateTime().Should().Be(issuedAtUtc);
        root.GetProperty("adminToken").GetString().Should().Be("admin-token");
        root.GetProperty("buyerTokens").GetArrayLength().Should().Be(3);
        root.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("issuedAtUtc", "adminToken", "buyerTokens");
    }
}
