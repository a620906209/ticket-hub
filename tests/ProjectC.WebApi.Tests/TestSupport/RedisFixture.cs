using System.Net;
using System.Net.Sockets;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace ProjectC.WebApi.Tests.TestSupport;

// 獨立於 ProjectC.Infrastructure.Tests.TestSupport.RedisFixture 的平行實作，比照
// CustomWebApplicationFactory 與 PostgresFixture 各自獨立管理自己的 Testcontainers 容器的既定作法
// （見 PostgresFixture 註解），不跨測試專案共用 fixture。
public sealed class RedisFixture : IAsyncLifetime
{
    private static readonly string? ComposeNetworkName =
        Environment.GetEnvironmentVariable("Testcontainers__ComposeNetworkName");

    private readonly string _networkAlias = $"projectc-test-redis-{Guid.NewGuid():N}";
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

    public IConnectionMultiplexer CreateConnection()
    {
        var options = ConfigurationOptions.Parse(ConnectionString);
        options.AbortOnConnectFail = false;
        return ConnectionMultiplexer.Connect(options);
    }

    public Task StopContainerAsync() => _container.StopAsync();

    public Task StartContainerAsync() => _container.StartAsync();
}

[CollectionDefinition(Name)]
public sealed class RedisCollection : ICollectionFixture<RedisFixture>
{
    public const string Name = "Redis";
}
