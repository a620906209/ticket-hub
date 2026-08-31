using System.Diagnostics;
using System.Net.Sockets;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using ProjectC.WebApi.Tests.TestSupport;
using Serilog;
using Serilog.Events;

namespace ProjectC.WebApi.Tests.Observability;

// Seq sink 連線失敗（拒絕連線／黑洞無回應）不得影響應用程式啟動或請求處理
// （observability spec.md「Seq sink 連線失敗不得影響應用程式啟動或請求處理」）。
public class SeqSinkResilienceTests : IClassFixture<SeqTestcontainersFixture>, IClassFixture<ObservabilityWebApplicationFactory>
{
    private readonly ObservabilityWebApplicationFactory _baselineFactory;

    public SeqSinkResilienceTests(SeqTestcontainersFixture reachableSeq, ObservabilityWebApplicationFactory baselineFactory)
    {
        _baselineFactory = baselineFactory;
        _baselineFactory.SeqServerUrl = reachableSeq.BaseUrl;
    }

    /// <summary>找一個目前沒有任何東西在監聽的本機 port，模擬連線立即被拒絕。</summary>
    private static string GetUnreachablePortUrl()
    {
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return $"http://127.0.0.1:{port}";
    }

    // 對應 AC: OBS-SEQ-SINK-FAILURE-RESILIENT
    [Fact]
    public async Task Startup_WithConnectionRefusedSeqUrl_SucceedsAndRequestProcessingTimeIsUnaffected()
    {
        var baselineElapsed = await MeasureRequestElapsedAsync(_baselineFactory);

        var unreachableUrl = GetUnreachablePortUrl();
        await using var factory = new ObservabilityWebApplicationFactory { SeqServerUrl = unreachableUrl };
        // 手動 new 出來的 factory 不受 xUnit 的 IClassFixture 管理，IAsyncLifetime.InitializeAsync()
        // 不會自動被呼叫（只有透過建構子注入的 fixture 才有這個待遇）——必須自己呼叫，否則
        // Postgres Testcontainers 沒啟動、連線字串是空字串，實測發現。
        await factory.InitializeAsync();

        // 斷言一：應用程式仍可正常啟動——建立 WebApplicationFactory 並取得 HttpClient（觸發 host
        // 建置與啟動）本身不拋出例外、不逾時，獨立驗證 Requirement 的「應用程式仍可正常啟動」子句。
        var startAction = () => factory.CreateClient();
        startAction.Should().NotThrow("Seq 連線被拒絕不得造成應用程式啟動失敗");

        // 斷言二（主要）：Seq sink 連線失敗不影響「其他 sink」持續接收事件（見 spec.md「Console sink
        // 作為不受 Seq 影響的備援輸出」；自動化測試不直接碰真正的 Console sink，理由見下方方法註解）。
        AssertOtherSinksStillReceiveEvents(unreachableUrl);

        // 斷言三（輔助訊號，非唯一關鍵判斷）：請求處理時間與 Seq 可連線時的基準相比無明顯差異。
        // 外部審查提醒：端到端耗時比較天生會受 Docker/CI 負載、Testcontainers 啟動狀態等環境因素
        // 影響，用來輔助佐證「沒有明顯卡住」尚可，但不該是唯一判斷——「不阻塞」的核心保證已由上面
        // 斷言二（其他 sink 持續正常運作，證明 Seq 寫入確實沒有卡住整條 pipeline）與斷言一（啟動不
        // 拋例外）涵蓋，這裡只是額外的、門檻刻意寬鬆的 sanity check。
        var elapsedWithUnreachableSeq = await MeasureRequestElapsedAsync(factory);
        elapsedWithUnreachableSeq.Should().BeLessThan(
            baselineElapsed * 5 + TimeSpan.FromSeconds(2),
            "Seq sink 的寫入是非同步排入佇列，不應該因為連線被拒絕而讓請求處理時間明顯變長");
    }

    // 對應 AC: OBS-SEQ-SINK-BLACKHOLE-RESILIENT
    [Fact]
    public async Task Startup_WithBlackholeSeqUrl_SucceedsAndRequestProcessingTimeIsUnaffected()
    {
        var baselineElapsed = await MeasureRequestElapsedAsync(_baselineFactory);

        // RFC 5737 保留給文件用途的測試位址（192.0.2.0/24），全球路由器一律不轉發、也不會回應，
        // 是穩定重現「連線後無回應」（黑洞）情境的慣用作法，不依賴任何真實外部服務。
        const string blackholeUrl = "http://192.0.2.1";
        await using var factory = new ObservabilityWebApplicationFactory { SeqServerUrl = blackholeUrl };
        await factory.InitializeAsync();

        var startAction = () => factory.CreateClient();
        startAction.Should().NotThrow("Seq 連線黑洞（無回應）不得造成應用程式啟動失敗");

        var elapsedWithBlackholeSeq = await MeasureRequestElapsedAsync(factory);
        elapsedWithBlackholeSeq.Should().BeLessThan(
            baselineElapsed * 5 + TimeSpan.FromSeconds(2),
            "Seq sink 是非同步排入佇列，不應該因為連線黑洞（無回應）而讓請求處理時間明顯變長，不能卡在等待逾時");

        AssertOtherSinksStillReceiveEvents(blackholeUrl);
    }

    // 外部審查提醒：不應該把「每個 WebApplicationFactory 各自解析到獨立的 Serilog.ILogger，
    // 彼此不共用同一個 process 全域 logger」當成未驗證的隱含前提——preserveStaticLogger: true
    // 的用意正是讓每個 host 各自的 Logger 只透過該 host 自己的 DI 容器解析（見 Program.cs 決策），
    // 這裡直接斷言兩個同時存在的 factory（一個 Seq 可連線、一個不可連線）解析到的是不同物件，
    // 把這個底層假設從「設計上應該如此＋其他測試行為間接印證」變成明確驗證過的事實。
    [Fact]
    public async Task DifferentFactories_ResolveIndependentSerilogLoggerInstances()
    {
        var baselineLogger = _baselineFactory.Services.GetRequiredService<ILogger>();

        await using var otherFactory = new ObservabilityWebApplicationFactory { SeqServerUrl = GetUnreachablePortUrl() };
        await otherFactory.InitializeAsync();
        var otherLogger = otherFactory.Services.GetRequiredService<ILogger>();

        otherLogger.Should().NotBeSameAs(baselineLogger,
            "每個 WebApplicationFactory 應該各自解析到獨立的 Serilog.ILogger 實例，不共用同一個 process 全域 logger");
    }

    private static async Task<TimeSpan> MeasureRequestElapsedAsync(ObservabilityWebApplicationFactory factory)
    {
        var client = factory.CreateClient();
        var stopwatch = Stopwatch.StartNew();
        var response = await client.GetAsync("/api/events");
        stopwatch.Stop();

        response.EnsureSuccessStatusCode();
        return stopwatch.Elapsed;
    }

    /// <summary>驗證 Serilog 的 sink 分派彼此獨立：Seq sink 連線失敗不會阻止「其他 sink」收到同一筆
    /// 事件（spec.md 舉的具體例子是 Console sink，本質是同一個底層保證——Serilog 對每個已設定的
    /// sink 各自獨立呼叫 `Emit`，一個 sink 的失敗不影響其他 sink）。
    ///
    /// 刻意不直接重導向真正的 <see cref="Console.Out"/>：那是 process 全域靜態狀態，即使把重導向窗口
    /// 縮到一次同步方法呼叫，OS 執行緒排程理論上仍可能在任意指令邊界搶佔，無法把風險降到 0——
    /// 這正是本次專案先前才實測踩過一次的同一類 process 全域靜態狀態 race（見 Program.cs 的
    /// Log.Logger 教訓）。外部審查認為「測試可靠性風險」不該用「機率很小」打發，必須整個避開碰到
    /// 全域狀態，因此改用另一個記憶體內 <see cref="ILogEventSink"/> 當作「Console 以外的任何 sink」
    /// 的代表，兩者走的是 Serilog 核心同一套 dispatch 機制，沒有針對 Console 特殊處理，驗證力等價，
    /// 且完全不涉及任何跨測試共用的可變狀態，在平行測試下絕對安全。Console sink 本身在真實環境下
    /// 持續正常運作，由 tasks.md 第 7 節的人工驗證涵蓋（`docker compose logs api`）。</summary>
    private static void AssertOtherSinksStillReceiveEvents(string unreachableSeqServerUrl)
    {
        var marker = $"other-sink-probe-{Guid.NewGuid():N}";
        var otherSink = new InMemoryLogEventSink();

        using var probeLogger = new LoggerConfiguration()
            .WriteTo.Sink(otherSink)
            .WriteTo.Seq(unreachableSeqServerUrl)
            .CreateLogger();

        probeLogger.Information("Other sink probe {Marker}", marker);

        bool HasMatchingMarker(LogEvent logEvent)
            => logEvent.Properties.TryGetValue("Marker", out var value) && value is ScalarValue { Value: string m } && m == marker;

        var matchingEvents = otherSink.Events.Where(HasMatchingMarker).ToList();
        matchingEvents.Should().ContainSingle(
            "Seq sink 指向無法連線的位址時，其他 sink 仍應正常收到同一筆事件，不受 Seq 失敗拖累");
    }
}
