using System.IdentityModel.Tokens.Jwt;
using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using ProjectC.Application.Common;
using ProjectC.Application.Common.Interfaces;
using ProjectC.Domain.Members;
using ProjectC.Domain.Organizers;
using ProjectC.Infrastructure.Security;
using ProjectC.LoadTest.Seeder.Tests.TestSupport;

namespace ProjectC.LoadTest.Seeder.Tests;

/// <summary>
/// k6-load-test spec「壓測 seeder 準備壓測帳號與 token 檔」：k6 無法走真實登入（需要驗證碼），
/// 壓測能否成立完全取決於 seeder 產出的 token 是否被 api 接受、是否對應 500 個不同買家；
/// 而 token 檔含有效 JWT，任何異常狀態都必須 fail loud 且不寫檔、成功時也不得把 token 印到輸出。
/// 直接呼叫 runner（繞過進入點的 Host == db 檢查，測試容器的 Host 不是 db）。
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class LoadTestSeedRunnerTests : IDisposable
{
    private const string AdminPassword = "Known-Test-Admin-Passw0rd-For-Leak-Check";

    private static readonly JwtOptions JwtOptions = new()
    {
        Issuer = "loadtest-tests-issuer",
        Audience = "loadtest-tests-audience",
        SigningKey = "loadtest-tests-signing-key-0123456789abcdef",
        AccessTokenExpirationMinutes = 30,
    };

    private readonly PostgresFixture _fixture;
    private readonly string _outputDirectory = Path.Combine(Path.GetTempPath(), $"seeder-runner-{Guid.NewGuid():N}");
    private readonly MutableDateTimeProvider _dateTimeProvider = new();

    public LoadTestSeedRunnerTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    public void Dispose()
    {
        if (Directory.Exists(_outputDirectory))
            Directory.Delete(_outputDirectory, recursive: true);
    }

    private string TokenFilePath => Path.Combine(_outputDirectory, "nested", "tokens.json");

    private sealed class MutableDateTimeProvider : IDateTimeProvider
    {
        public DateTime UtcNow { get; set; } = new(2026, 10, 5, 8, 0, 0, DateTimeKind.Utc);
    }

    private sealed record RunResult(int ExitCode, string Output, string Error);

    private async Task<RunResult> RunSeederAsync(string connectionString, int buyerCount)
    {
        await using var dbContext = PostgresFixture.CreateDbContext(connectionString);
        var runner = new LoadTestSeedRunner(
            dbContext,
            new BCryptPasswordHasher(),
            new JwtTokenService(Options.Create(JwtOptions)),
            _dateTimeProvider,
            NullLoggerFactory.Instance);
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await runner.RunAsync(
            new LoadTestSeedRequest(buyerCount, TokenFilePath, AdminPassword), output, error, CancellationToken.None);

        return new RunResult(exitCode, output.ToString(), error.ToString());
    }

    private LoadTestTokenFile ReadTokenFile() => LoadTestTokenFile.Deserialize(File.ReadAllText(TokenFilePath));

    // 與 src/ProjectC.WebApi/Program.cs 的 AddJwtBearer 設定相同：api 用這組參數驗證，token 才能通過。
    private static JwtSecurityToken ValidateLikeProductApi(string token)
    {
        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = JwtOptions.Issuer,
            ValidateAudience = true,
            ValidAudience = JwtOptions.Audience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JwtOptions.SigningKey)),
            ClockSkew = TimeSpan.FromSeconds(30),
        };
        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        handler.ValidateToken(token, parameters, out var validatedToken);
        return (JwtSecurityToken)validatedToken;
    }

    [Fact]
    public async Task RunAsync_LT_SEED_001_OnFirstRun_CreatesBuyersAdminAndValidTokens()
    {
        var connectionString = await _fixture.CreateDatabaseAsync(applyMigrations: true);

        var result = await RunSeederAsync(connectionString, buyerCount: 5);

        result.ExitCode.Should().Be(0, result.Error);
        await using var dbContext = PostgresFixture.CreateDbContext(connectionString);
        var buyers = await dbContext.Members.Where(m => m.Email.StartsWith("loadtest-buyer-")).ToListAsync();
        buyers.Should().HaveCount(5);
        buyers.Should().OnlyContain(m => m.IsActive && m.Role == MemberRole.Member && m.Email.EndsWith("@loadtest.invalid"));

        var admins = await dbContext.Members.Where(m => m.Email == LoadTestSeedRunner.AdminEmail).ToListAsync();
        admins.Should().ContainSingle().Which.Role.Should().Be(MemberRole.Admin);
        var approvedOrganizerIds = await dbContext.OrganizerMembers
            .Where(om => om.MemberId == admins[0].Id && om.Role == OrganizerMemberRole.Owner)
            .Join(dbContext.Organizers, om => om.OrganizerId, o => o.Id, (_, o) => o)
            .Where(o => o.Status == OrganizerStatus.Approved)
            .Select(o => o.Id)
            .ToListAsync();
        approvedOrganizerIds.Should().ContainSingle();

        var tokenFile = ReadTokenFile();
        tokenFile.BuyerTokens.Should().HaveCount(5).And.OnlyHaveUniqueItems();
        var buyerSubjects = tokenFile.BuyerTokens.Select(ValidateLikeProductApi).ToList();
        buyerSubjects.Select(t => t.Subject).Should().OnlyHaveUniqueItems("每個 VU 必須代表不同買家")
            .And.BeEquivalentTo(buyers.Select(b => b.Id.ToString()));
        buyerSubjects.Should().OnlyContain(t => !t.Claims.Any(c => c.Type == CustomClaimTypes.OrganizerId),
            "買家 token 不帶 OrganizerId");

        var adminToken = ValidateLikeProductApi(tokenFile.AdminToken);
        adminToken.Subject.Should().Be(admins[0].Id.ToString());
        adminToken.Claims.Should().ContainSingle(c => c.Type == CustomClaimTypes.OrganizerId)
            .Which.Value.Should().Be(approvedOrganizerIds[0].ToString(), "admin API 的 policy 只看合法 OrganizerId claim");
    }

    [Fact]
    public async Task RunAsync_LT_SEED_002_WhenRunAgain_DoesNotDuplicateMembersAndOverwritesTokenFile()
    {
        var connectionString = await _fixture.CreateDatabaseAsync(applyMigrations: true);
        (await RunSeederAsync(connectionString, buyerCount: 5)).ExitCode.Should().Be(0);
        var firstTokenFile = ReadTokenFile();

        _dateTimeProvider.UtcNow = _dateTimeProvider.UtcNow.AddMinutes(5);
        var result = await RunSeederAsync(connectionString, buyerCount: 5);

        result.ExitCode.Should().Be(0, result.Error);
        await using var dbContext = PostgresFixture.CreateDbContext(connectionString);
        (await dbContext.Members.CountAsync(m => m.Email.StartsWith("loadtest-buyer-"))).Should().Be(5);
        (await dbContext.Members.CountAsync(m => m.Email == LoadTestSeedRunner.AdminEmail)).Should().Be(1);
        (await dbContext.Organizers.CountAsync(o => o.Name == LoadTestSeedRunner.OrganizerName)).Should().Be(1);

        var secondTokenFile = ReadTokenFile();
        secondTokenFile.IssuedAtUtc.Should().Be(_dateTimeProvider.UtcNow);
        secondTokenFile.IssuedAtUtc.Should().BeAfter(firstTokenFile.IssuedAtUtc);
        secondTokenFile.BuyerTokens.Should().NotIntersectWith(firstTokenFile.BuyerTokens, "每次執行都重新簽發 token");
    }

    [Fact]
    public async Task RunAsync_LT_SEED_004_OnSuccess_OutputContainsNoTokenOrPassword()
    {
        var connectionString = await _fixture.CreateDatabaseAsync(applyMigrations: true);

        var result = await RunSeederAsync(connectionString, buyerCount: 5);

        result.ExitCode.Should().Be(0, result.Error);
        var tokenFile = ReadTokenFile();
        var allOutput = result.Output + result.Error;
        allOutput.Should().NotBeNullOrWhiteSpace();
        foreach (var token in tokenFile.BuyerTokens.Append(tokenFile.AdminToken))
            allOutput.Should().NotContain(token);
        allOutput.Should().NotContain(AdminPassword);
        result.Output.Should().Contain("5").And.Contain(TokenFilePath, "輸出只有筆數與檔案路徑等摘要");
    }

    [Fact]
    public async Task RunAsync_WhenOutputDirectoryDoesNotExist_CreatesItAndWritesTokenFile()
    {
        var connectionString = await _fixture.CreateDatabaseAsync(applyMigrations: true);
        Directory.Exists(Path.GetDirectoryName(TokenFilePath)).Should().BeFalse("loadtest/.output 被 gitignore，新 clone 時不存在");

        var result = await RunSeederAsync(connectionString, buyerCount: 1);

        result.ExitCode.Should().Be(0, result.Error);
        File.Exists(TokenFilePath).Should().BeTrue();
    }

    [Fact]
    // 測試在 Linux 容器內執行；Unix 檔案權限 API 在 Windows 不支援。
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task RunAsync_WhenOutputDirectoryWasRootOwned_GivesItOnlyToK6SoK6CanWriteAndOthersCannotList()
    {
        var connectionString = await _fixture.CreateDatabaseAsync(applyMigrations: true);
        // 模擬先前以 root 0755 建好的目錄：k6 容器以 uid 12345 執行，不是擁有者就無法輸出 summary JSON；
        // 但改成所有人可寫會讓其他使用者也能列出／讀取 token 檔，所以只交給 k6。
        var rootDefaultMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
        var directory = Path.GetDirectoryName(TokenFilePath)!;
        Directory.CreateDirectory(directory, rootDefaultMode);

        var result = await RunSeederAsync(connectionString, buyerCount: 1);

        result.ExitCode.Should().Be(0, result.Error);
        File.GetUnixFileMode(directory).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        ReadOwner(directory).Should().Be("12345:12345");
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task RunAsync_WhenOldWorldReadableTokenFileExists_ReplacesItWithK6OnlyFile()
    {
        var connectionString = await _fixture.CreateDatabaseAsync(applyMigrations: true);
        // token 檔含有效 Admin／買家 JWT：舊版留下的 0644 檔若被原地覆寫會沿用舊權限，必須換成只有 k6 能讀的新檔。
        Directory.CreateDirectory(Path.GetDirectoryName(TokenFilePath)!);
        await File.WriteAllTextAsync(TokenFilePath, "stale");
        File.SetUnixFileMode(TokenFilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        var result = await RunSeederAsync(connectionString, buyerCount: 1);

        result.ExitCode.Should().Be(0, result.Error);
        File.GetUnixFileMode(TokenFilePath).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        ReadOwner(TokenFilePath).Should().Be("12345:12345");
        (await File.ReadAllTextAsync(TokenFilePath)).Should().NotBe("stale");
        File.Exists(TokenFilePath + ".tmp").Should().BeFalse();
    }

    // .NET 沒有讀取檔案擁有者的 API；測試在 Linux 容器內執行，用 coreutils stat。
    private static string ReadOwner(string path)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("stat", ["-c", "%u:%g", path])
        {
            RedirectStandardOutput = true,
        })!;
        var owner = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        process.ExitCode.Should().Be(0);
        return owner;
    }

    [Fact]
    public async Task RunAsync_LT_SEED_006_WithPendingMigrations_FailsWithoutWritingDbOrTokenFile()
    {
        var connectionString = await _fixture.CreateDatabaseAsync(applyMigrations: false);

        var result = await RunSeederAsync(connectionString, buyerCount: 5);

        result.ExitCode.Should().NotBe(0);
        (result.Output + result.Error).Should().Contain("docker compose exec api dotnet ef database update");
        File.Exists(TokenFilePath).Should().BeFalse();
        await using var dbContext = PostgresFixture.CreateDbContext(connectionString);
        var tableCount = await dbContext.Database
            .SqlQuery<int>($"SELECT COUNT(*)::int AS \"Value\" FROM information_schema.tables WHERE table_schema = 'public'")
            .SingleAsync();
        tableCount.Should().Be(0, "偵測 migration 只做唯讀查詢，不得建立任何資料表");
    }

    [Fact]
    public async Task RunAsync_LT_SEED_008_WhenAdminEmailBelongsToNonAdmin_FailsWithoutWritingTokenFile()
    {
        var connectionString = await _fixture.CreateDatabaseAsync(applyMigrations: true);
        await using (var dbContext = PostgresFixture.CreateDbContext(connectionString))
        {
            dbContext.Members.Add(Member.Register(LoadTestSeedRunner.AdminEmail, "Not An Admin", "hash"));
            await dbContext.SaveChangesAsync();
        }

        var result = await RunSeederAsync(connectionString, buyerCount: 5);

        result.ExitCode.Should().NotBe(0);
        result.Error.Should().Contain("not an Admin");
        File.Exists(TokenFilePath).Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_LT_SEED_008_WhenAdminOwnsTwoLoadTestOrganizers_FailsWithoutWritingTokenFile()
    {
        var connectionString = await _fixture.CreateDatabaseAsync(applyMigrations: true);
        (await RunSeederAsync(connectionString, buyerCount: 1)).ExitCode.Should().Be(0);
        File.Delete(TokenFilePath);
        await using (var dbContext = PostgresFixture.CreateDbContext(connectionString))
        {
            var admin = await dbContext.Members.SingleAsync(m => m.Email == LoadTestSeedRunner.AdminEmail);
            var organizer = Organizer.Apply(Guid.NewGuid(), LoadTestSeedRunner.OrganizerName, admin.Id, _dateTimeProvider.UtcNow);
            organizer.Approve(admin.Id, _dateTimeProvider.UtcNow);
            dbContext.Organizers.Add(organizer);
            dbContext.OrganizerMembers.Add(new OrganizerMember(Guid.NewGuid(), organizer.Id, admin.Id, OrganizerMemberRole.Owner));
            await dbContext.SaveChangesAsync();
        }

        var result = await RunSeederAsync(connectionString, buyerCount: 1);

        result.ExitCode.Should().NotBe(0);
        result.Error.Should().Contain("found 2");
        File.Exists(TokenFilePath).Should().BeFalse();
    }
}
