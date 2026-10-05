using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ProjectC.Infrastructure.Persistence;
using ProjectC.LoadTest.Seeder;

// 明確只讀環境變數（compose 注入的 ASPNETCORE_ENVIRONMENT、ConnectionStrings__*、Jwt__*），
// 不依賴 Host builder 預設會額外載入的 appsettings／user secrets（k6-load-test design.md 決策 1）。
var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();

using var cancellationTokenSource = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellationTokenSource.Cancel();
};

return await SeederEntryPoint.RunAsync(
    new SeederEntryPointContext(
        args,
        configuration,
        connectionString => new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(connectionString).Options),
        SeederEntryPoint.TokenFilePath,
        Console.Out,
        Console.Error),
    cancellationTokenSource.Token);
