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
