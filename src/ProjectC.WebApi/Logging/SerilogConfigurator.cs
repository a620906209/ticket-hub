using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Core;

namespace ProjectC.WebApi.Logging;

// 供 Program.cs 唯一一次呼叫的 UseSerilog 設定邏輯。測試需要額外掛一個記憶體 sink 時，不透過
// 二次呼叫 UseSerilog（實測發現：WebApplicationFactory.CreateHost 再呼叫一次 UseSerilog 會與
// Program.cs 原本這次互相干擾，部分日誌管線各自為政、記憶體 sink 收不到所有事件），改成從
// UseSerilog 回呼本來就會提供的 IServiceProvider 解析一個測試端預先註冊好的 ILogEventSink——
// 這是該多載 services 參數本來就支援的用法（observability tasks.md 4.1）。
public static class SerilogConfigurator
{
    private const string SerilogSection = "Serilog";
    private const string WriteToKey = "Serilog:WriteTo";
    private const string AuditToKey = "Serilog:AuditTo";
    private const string UsingKey = "Serilog:Using";

    // Bootstrap logger 同樣以 ExceptionMaskingSink 包住 Console：host 建置失敗時 Program.cs 的 Log.Fatal 帶著例外，
    // 其文字也要遮蔽（exception-log-masking design.md 決策 5）。維持 CreateBootstrapLogger 的 ReloadableLogger 語意不變。
    public static Serilog.ILogger CreateBootstrapLogger()
    {
        var consoleLogger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Console()
            .CreateLogger();

        return new LoggerConfiguration()
            .WriteTo.Sink(new ExceptionMaskingSink(consoleLogger))
            .CreateBootstrapLogger();
    }

    // 外層只保留等級／override／enricher 與 ExceptionMaskingSink；所有輸出 sink（WriteTo、Seq、DI sink）放內層，
    // 內層等級為 Verbose，讓等級過濾只發生在外層（exception-log-masking design.md 決策 1）。
    public static void Configure(HostBuilderContext context, IServiceProvider services, LoggerConfiguration loggerConfiguration)
    {
        var innerConfiguration = new LoggerConfiguration()
            .ReadFrom.Configuration(BuildSection(context.Configuration, isInner: true))
            .MinimumLevel.Verbose();

        // Seq sink 獨立用 "Seq:ServerUrl" 這個純字串設定值判斷是否啟用，見 Program.cs 同一段註解。
        var seqServerUrl = context.Configuration["Seq:ServerUrl"];
        if (!string.IsNullOrWhiteSpace(seqServerUrl))
        {
            innerConfiguration.WriteTo.Seq(seqServerUrl);
        }

        var additionalSink = services.GetService<ILogEventSink>();
        if (additionalSink is not null)
        {
            innerConfiguration.WriteTo.Sink(additionalSink);
        }

        loggerConfiguration
            .ReadFrom.Configuration(BuildSection(context.Configuration, isInner: false))
            .Enrich.FromLogContext()
            .WriteTo.Sink(new ExceptionMaskingSink(innerConfiguration.CreateLogger()));
    }

    // 複製 Serilog 區段而不是原地修改：保留環境變數等所有設定來源合併後的結果，並讓兩個 logger 各讀到互不重疊的部分。
    private static IConfiguration BuildSection(IConfiguration source, bool isInner)
    {
        var values = new Dictionary<string, string?>();
        foreach (var (key, value) in source.GetSection(SerilogSection).AsEnumerable(makePathsRelative: false))
        {
            if (value is null)
            {
                continue;
            }

            var isInnerOnly = IsKeyOrChild(key, WriteToKey) || IsKeyOrChild(key, AuditToKey) || IsKeyOrChild(key, UsingKey);
            var isWriteToOrAuditTo = IsKeyOrChild(key, WriteToKey) || IsKeyOrChild(key, AuditToKey);
            if (isInner ? isInnerOnly : !isWriteToOrAuditTo)
            {
                values[key] = value;
            }
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    // 精確比對 key 或其子鍵：StartsWith 會把 "Serilog:WriteToXxx" 這類不相干的 key 也算進去。
    private static bool IsKeyOrChild(string key, string parent)
    {
        return key.Equals(parent, StringComparison.OrdinalIgnoreCase)
            || key.StartsWith(parent + ":", StringComparison.OrdinalIgnoreCase);
    }
}
