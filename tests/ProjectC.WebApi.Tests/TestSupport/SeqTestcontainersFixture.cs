using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace ProjectC.WebApi.Tests.TestSupport;

// 比照 CustomWebApplicationFactory 的 PostgreSqlContainer 用法：啟動一個獨立、與 docker-compose.yml
// 定義的 seq 服務完全無關的測試專用 Seq 容器（observability design.md 決策 8）。用 compose 網路別名
// （非 host-mapped port）連線——Docker Desktop for Windows 下 host-published port 無法跨 bridge 網路
// 連線，dotnet test 本身在 api 容器內執行（本專案固定如此），必須掛進同一個 compose 網路才能互連
// （比照既有 PostgreSqlContainer 用法的既定理由）。
public sealed class SeqTestcontainersFixture : IAsyncLifetime
{
    private static readonly string? ComposeNetworkName =
        Environment.GetEnvironmentVariable("Testcontainers__ComposeNetworkName");

    private readonly string _networkAlias = $"projectc-test-seq-{Guid.NewGuid():N}";
    private readonly IContainer _container;

    /// <summary>Seq Web/ingestion 對外位址（container port 80），<see cref="InitializeAsync"/> 完成後才有正確值。</summary>
    public string BaseUrl { get; private set; } = string.Empty;

    public SeqTestcontainersFixture()
    {
        // 用日誌訊息判斷就緒（比照既有 PostgreSqlContainer 的 pg_isready 手法——不透過 host-mapped
        // port 的網路探測）：實測發現 UntilHttpRequestIsSucceeded 這類外部 HTTP 探測，在 dotnet test
        // 本身於 api 容器內執行時，會嘗試連線 host-mapped port，但 Docker Desktop for Windows 下這種
        // 跨容器連線本來就不通（同一個既有理由，見 CustomWebApplicationFactory 對 PostgreSqlContainer
        // 的既定註解），導致永遠等不到就緒、整個測試行程卡死。UntilMessageIsLogged 改讀容器日誌
        // （透過 Docker API，不受這個網路限制），"Ingestion enabled" 是 Seq 完成初始化後穩定會印出的訊息。
        // 版本跟 docker-compose.yml 的 seq 服務釘選同一個已驗證版本，避免兩邊行為不一致。
        var builder = new ContainerBuilder("datalust/seq:2026.1.17114")
            .WithEnvironment("ACCEPT_EULA", "Y")
            .WithEnvironment("SEQ_FIRSTRUN_NOAUTHENTICATION", "True")
            .WithPortBinding(80, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Ingestion enabled"));

        if (ComposeNetworkName is not null)
        {
            builder = builder.WithNetwork(ComposeNetworkName).WithNetworkAliases(_networkAlias);
        }

        _container = builder.Build();
    }

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        BaseUrl = ComposeNetworkName is not null
            ? $"http://{_networkAlias}:80"
            : $"http://localhost:{_container.GetMappedPublicPort(80)}";
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}
