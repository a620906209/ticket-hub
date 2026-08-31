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
    public static void Configure(HostBuilderContext context, IServiceProvider services, LoggerConfiguration loggerConfiguration)
    {
        loggerConfiguration
            .ReadFrom.Configuration(context.Configuration)
            .Enrich.FromLogContext();

        // Seq sink 獨立用 "Seq:ServerUrl" 這個純字串設定值判斷是否啟用，見 Program.cs 同一段註解。
        var seqServerUrl = context.Configuration["Seq:ServerUrl"];
        if (!string.IsNullOrWhiteSpace(seqServerUrl))
        {
            loggerConfiguration.WriteTo.Seq(seqServerUrl);
        }

        var additionalSink = services.GetService<ILogEventSink>();
        if (additionalSink is not null)
        {
            loggerConfiguration.WriteTo.Sink(additionalSink);
        }
    }
}
