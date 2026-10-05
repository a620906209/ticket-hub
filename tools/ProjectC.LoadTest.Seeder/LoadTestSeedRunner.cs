using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProjectC.Application.Common.Interfaces;
using ProjectC.Domain.Members;
using ProjectC.Domain.Organizers;
using ProjectC.Infrastructure.Persistence;
using ProjectC.Infrastructure.Persistence.Seeding;

namespace ProjectC.LoadTest.Seeder;

public sealed record LoadTestSeedRequest(int BuyerCount, string TokenFilePath, string AdminPassword);

/// <summary>
/// 建立壓測 Admin（重用 <see cref="DevelopmentDataSeeder"/>）與壓測買家，簽發 access token 並寫入 token 檔
/// （k6-load-test design.md 決策 2、3、4）。任一檢查失敗都不寫 token 檔。
/// </summary>
public sealed class LoadTestSeedRunner
{
    public const string AdminEmail = "loadtest-admin@loadtest.invalid";
    public const string OrganizerName = "LoadTest Organizer";
    public const int FailureExitCode = 1;

    private const string BuyerDisplayName = "LoadTest Buyer";

    private readonly ApplicationDbContext _dbContext;
    private readonly IPasswordHasher _passwordHasher;
    private readonly ITokenService _tokenService;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly ILoggerFactory _loggerFactory;

    public LoadTestSeedRunner(
        ApplicationDbContext dbContext,
        IPasswordHasher passwordHasher,
        ITokenService tokenService,
        IDateTimeProvider dateTimeProvider,
        ILoggerFactory loggerFactory)
    {
        _dbContext = dbContext;
        _passwordHasher = passwordHasher;
        _tokenService = tokenService;
        _dateTimeProvider = dateTimeProvider;
        _loggerFactory = loggerFactory;
    }

    public static string FormatBuyerEmail(int index) => $"loadtest-buyer-{index:D4}@loadtest.invalid";

    public async Task<int> RunAsync(LoadTestSeedRequest request, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        // DevelopmentDataSeeder 遇到 pending migration 只記 warning 就 return，不先擋的話後續會以「找不到 Admin」這種
        // 不明確的方式失敗（design.md「安全確認／資料庫」）。
        var pendingMigrations = await _dbContext.Database.GetPendingMigrationsAsync(cancellationToken);
        if (pendingMigrations.Any())
        {
            await error.WriteLineAsync("Database has pending migrations. Run `docker compose exec api dotnet ef database update` first.");
            return FailureExitCode;
        }

        await CreateDevelopmentDataSeeder(request.AdminPassword).SeedAsync(cancellationToken);

        var (admin, organizerId, adminError) = await FindLoadTestAdminAsync(cancellationToken);
        if (adminError is not null)
        {
            await error.WriteLineAsync(adminError);
            return FailureExitCode;
        }

        var buyers = await EnsureBuyersAsync(request.BuyerCount, cancellationToken);

        var tokenFile = new LoadTestTokenFile(
            _dateTimeProvider.UtcNow,
            _tokenService.GenerateAccessToken(admin!, organizerId),
            buyers.Select(buyer => _tokenService.GenerateAccessToken(buyer)).ToList());

        await TokenFileWriter.WriteAsync(request.TokenFilePath, tokenFile.Serialize(), cancellationToken);

        await output.WriteLineAsync($"Seeded {buyers.Count} buyer token(s) and 1 admin token to {request.TokenFilePath}");
        return 0;
    }

    private DevelopmentDataSeeder CreateDevelopmentDataSeeder(string adminPassword)
        => new(
            _dbContext,
            _passwordHasher,
            _dateTimeProvider,
            Options.Create(new DevelopmentSeedOptions
            {
                AdminEmail = AdminEmail,
                AdminPassword = adminPassword,
                AdminDisplayName = "LoadTest Admin",
                OrganizerName = OrganizerName,
            }),
            _loggerFactory.CreateLogger<DevelopmentDataSeeder>());

    // SeedAsync 不回報成功與否，所以一律事後查回並檢查（design.md 決策 2 的 runner 流程 3–5）。
    private async Task<(Member? Admin, Guid OrganizerId, string? Error)> FindLoadTestAdminAsync(CancellationToken cancellationToken)
    {
        var admin = await _dbContext.Members.SingleOrDefaultAsync(m => m.Email == AdminEmail, cancellationToken);
        if (admin is null)
            return (null, Guid.Empty, $"Load-test admin {AdminEmail} was not created.");

        // SeedAsync 遇到同 email 但非 Admin 的帳號不會改角色，代表壓測資料被人為改動過，fail loud 而不是沿用。
        if (admin.Role != MemberRole.Admin)
            return (null, Guid.Empty, $"Account {AdminEmail} exists but is not an Admin; refusing to issue tokens.");

        var organizerIds = await _dbContext.OrganizerMembers
            .Where(om => om.MemberId == admin.Id && om.Role == OrganizerMemberRole.Owner)
            .Join(_dbContext.Organizers, om => om.OrganizerId, o => o.Id, (_, o) => o)
            .Where(o => o.Status == OrganizerStatus.Approved && o.Name == OrganizerName)
            .Select(o => o.Id)
            .ToListAsync(cancellationToken);
        if (organizerIds.Count != 1)
            return (null, Guid.Empty, $"Expected exactly 1 approved '{OrganizerName}' owned by {AdminEmail}, found {organizerIds.Count}.");

        return (admin, organizerIds[0], null);
    }

    private async Task<List<Member>> EnsureBuyersAsync(int buyerCount, CancellationToken cancellationToken)
    {
        var emails = Enumerable.Range(1, buyerCount).Select(FormatBuyerEmail).ToList();
        var buyers = await _dbContext.Members.Where(m => emails.Contains(m.Email)).ToListAsync(cancellationToken);

        var existingEmails = buyers.Select(m => m.Email).ToHashSet();
        var missingEmails = emails.Where(email => !existingEmails.Contains(email)).ToList();
        if (missingEmails.Count > 0)
        {
            // BCrypt 刻意很慢，逐一雜湊 500 次要數分鐘；這些帳號永遠不以密碼登入，共用一次雜湊沒有安全損失（決策 3）。
            var sharedPasswordHash = _passwordHasher.HashPassword(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            var newBuyers = missingEmails.Select(email => Member.Register(email, BuyerDisplayName, sharedPasswordHash)).ToList();
            _dbContext.Members.AddRange(newBuyers);
            buyers.AddRange(newBuyers);
        }

        foreach (var buyer in buyers.Where(m => !m.IsActive))
            buyer.Activate();

        await _dbContext.SaveChangesAsync(cancellationToken);
        return buyers.OrderBy(m => m.Email, StringComparer.Ordinal).ToList();
    }
}
