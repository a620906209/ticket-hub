using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace ProjectC.Infrastructure.Tests.TestSupport;

// 依 CLAUDE.md 測試規範：整合測試用 Testcontainers 啟動獨立的 Redis 容器，比照既有 PostgresFixture
// 模式（purchase-queue-leader-election tasks.md 5.2）。dotnet test 若在 api 容器內執行，臨時容器要
// 掛進同一個 compose 網路才能互連（見 CustomWebApplicationFactory／PostgresFixture 踩過的坑）。
public sealed class RedisFixture : IAsyncLifetime
{
    private static readonly string? ComposeNetworkName =
        Environment.GetEnvironmentVariable("Testcontainers__ComposeNetworkName");

    private readonly string _networkAlias = $"projectc-infra-test-redis-{Guid.NewGuid():N}";
    private readonly RedisContainer _container;

    public string ConnectionString { get; private set; } = string.Empty;

    public RedisFixture()
    {
        var builder = new RedisBuilder("redis:7-alpine");

        if (ComposeNetworkName is not null)
            builder = builder.WithNetwork(ComposeNetworkName).WithNetworkAliases(_networkAlias);
        else
            // 未接 compose 網路（例如 CI runner）時改用固定 host port：預設的隨機 port 會在
            // StopContainerAsync／StartContainerAsync 重啟後被 Docker 重新分配，既有連線就連不回來。
            builder = builder.WithPortBinding(ReserveFreeHostPort(), RedisBuilder.RedisPort);

        _container = builder.Build();
    }

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        ConnectionString = ComposeNetworkName is not null
            ? $"{_networkAlias}:6379"
            : _container.GetConnectionString();
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();

    private static int ReserveFreeHostPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>停止／重新啟動同一個容器（保留同一個連線位址／port），用來模擬「Redis 不可用 → 恢復」
    /// 情境，比一次性 Dispose 更貼近真實故障後恢復的行為（PQLE-007／PQLE-009）。</summary>
    public Task StopContainerAsync() => _container.StopAsync();

    public Task StartContainerAsync() => _container.StartAsync();

    /// <summary>每次呼叫都回傳新的 IConnectionMultiplexer instance，模擬獨立的應用程式實例各自持有
    /// 自己的連線（比照 PurchaseQueueAdmissionServiceTests 的多實例測試手法）。</summary>
    public IConnectionMultiplexer CreateConnection()
    {
        var options = ConfigurationOptions.Parse(ConnectionString);
        options.AbortOnConnectFail = false;
        return ConnectionMultiplexer.Connect(options);
    }
}

[CollectionDefinition(Name)]
[SuppressMessage("Naming", "CA1711", Justification = "xUnit collection definition 的慣例命名（xUnit 文件範例即為 XxxCollection），不是集合型別。")]
public sealed class RedisCollection : ICollectionFixture<RedisFixture>
{
    public const string Name = "Redis";
}
