using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using ProjectC.Application.Common.Interfaces;
using ProjectC.Infrastructure.Persistence;
using ProjectC.Infrastructure.Security;

namespace ProjectC.LoadTest.Seeder;

/// <param name="CreateDbContext">以已驗證的連線字串建立 DbContext；所有檢查通過前不得呼叫（測試以此觀察「不建立 DbContext」）。</param>
/// <param name="TokenFilePath">正式執行固定為 <see cref="SeederEntryPoint.TokenFilePath"/>，不開放命令列參數指定。</param>
public sealed record SeederEntryPointContext(
    IReadOnlyList<string> Args,
    IConfiguration Configuration,
    Func<string, ApplicationDbContext> CreateDbContext,
    string TokenFilePath,
    TextWriter Output,
    TextWriter Error);

/// <summary>
/// 壓測 seeder 進入點：在建立 DbContext 之前完成全部環境與參數檢查，任一不符就以非 0 結束
/// （k6-load-test design.md 決策 1「安全邊界」）。
/// </summary>
public static class SeederEntryPoint
{
    public const string TokenFilePath = "/src/loadtest/.output/tokens.json";
    public const int InvalidArgumentsExitCode = 2;
    public const int InvalidEnvironmentExitCode = 1;

    private const string ComposeDatabaseHost = "db";
    private const int MinimumSigningKeyLength = 32;
    private const int DefaultBuyerCount = 500;
    private const int MinimumBuyerCount = 1;
    private const int MaximumBuyerCount = 1000;

    public static async Task<int> RunAsync(SeederEntryPointContext context, CancellationToken cancellationToken)
    {
        var environmentError = ValidateEnvironment(context.Configuration);
        if (environmentError is not null)
        {
            await context.Error.WriteLineAsync(environmentError);
            return InvalidEnvironmentExitCode;
        }

        var (buyerCount, argumentError) = ParseBuyerCount(context.Args);
        if (argumentError is not null)
        {
            await context.Error.WriteLineAsync(argumentError);
            return InvalidArgumentsExitCode;
        }

        await using var serviceProvider = BuildServiceProvider(context);
        await using var scope = serviceProvider.CreateAsyncScope();
        var runner = scope.ServiceProvider.GetRequiredService<LoadTestSeedRunner>();

        // 隨機密碼只在這裡產生、交給 runner，不輸出也不保存：壓測帳號永遠不以密碼登入。
        var adminPassword = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        return await runner.RunAsync(
            new LoadTestSeedRequest(buyerCount, context.TokenFilePath, adminPassword),
            context.Output,
            context.Error,
            cancellationToken);
    }

    private static string? ValidateEnvironment(IConfiguration configuration)
    {
        // 與 IHostEnvironment.IsDevelopment() 相同，環境名稱不分大小寫。
        if (!string.Equals(configuration["ASPNETCORE_ENVIRONMENT"], "Development", StringComparison.OrdinalIgnoreCase))
            return "ASPNETCORE_ENVIRONMENT must be Development; the load-test seeder only runs against local docker compose.";

        var hostError = ValidateDatabaseHost(configuration.GetConnectionString("DefaultConnection"));
        if (hostError is not null)
            return hostError;

        return ValidateJwtSection(configuration.GetSection(JwtOptions.SectionName));
    }

    private static string? ValidateDatabaseHost(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            return "ConnectionStrings:DefaultConnection is not configured.";

        string? host;
        try
        {
            host = new NpgsqlConnectionStringBuilder(connectionString).Host;
        }
        catch (ArgumentException)
        {
            // 不回顯原始字串：連線字串含資料庫密碼。
            return "ConnectionStrings:DefaultConnection is not a valid PostgreSQL connection string.";
        }

        return string.Equals(host, ComposeDatabaseHost, StringComparison.OrdinalIgnoreCase)
            ? null
            : $"Database host must be the compose service '{ComposeDatabaseHost}'; refusing to seed another database.";
    }

    private static string? ValidateJwtSection(IConfigurationSection jwtSection)
    {
        foreach (var key in new[] { nameof(JwtOptions.Issuer), nameof(JwtOptions.Audience), nameof(JwtOptions.SigningKey) })
        {
            if (string.IsNullOrWhiteSpace(jwtSection[key]))
                return $"Jwt:{key} is not configured.";
        }

        // 與產品 JwtOptions.SigningKey 的 [MinLength(32)] 相同，避免簽出 api 不接受的弱 token。
        return jwtSection[nameof(JwtOptions.SigningKey)]!.Length < MinimumSigningKeyLength
            ? $"Jwt:SigningKey must be at least {MinimumSigningKeyLength} characters."
            : null;
    }

    private static (int BuyerCount, string? Error) ParseBuyerCount(IReadOnlyList<string> args)
    {
        const string usage = "Usage: --buyers <1-1000> (default 500). No other arguments are supported.";

        if (args.Count == 0)
            return (DefaultBuyerCount, null);

        if (args.Count != 2 || args[0] != "--buyers")
            return (0, $"Unsupported arguments. {usage}");

        if (!int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var buyerCount)
            || buyerCount is < MinimumBuyerCount or > MaximumBuyerCount)
            return (0, $"--buyers must be an integer between {MinimumBuyerCount} and {MaximumBuyerCount}. {usage}");

        return (buyerCount, null);
    }

    private static ServiceProvider BuildServiceProvider(SeederEntryPointContext context)
    {
        var connectionString = context.Configuration.GetConnectionString("DefaultConnection")!;
        var jwtOptions = context.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>()!;

        var services = new ServiceCollection();
        // log 一律寫到 stderr：stdout 只留 runner 的摘要，方便比對輸出不含 token（design.md 決策 1）。
        services.AddLogging(builder => builder
            .SetMinimumLevel(LogLevel.Warning)
            .AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace));
        services.AddScoped(_ => context.CreateDbContext(connectionString));
        services.AddSingleton<IDateTimeProvider, SystemDateTimeProvider>();
        services.AddSingleton<IPasswordHasher, BCryptPasswordHasher>();
        services.AddSingleton(Options.Create(jwtOptions));
        services.AddSingleton<ITokenService, JwtTokenService>();
        services.AddScoped<LoadTestSeedRunner>();
        return services.BuildServiceProvider();
    }
}
