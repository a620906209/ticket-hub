using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Serilog;
using ProjectC.Infrastructure.Persistence.Seeding;
using ProjectC.WebApi.Common;
using ProjectC.WebApi.ExceptionHandling;
using ProjectC.WebApi.Extensions;
using ProjectC.WebApi.HealthChecks;
using ProjectC.WebApi.Logging;
using ProjectC.WebApi.OpenApi;

// Bootstrap logger：在 appsettings.json 的 Serilog 設定節被讀取之前先能輸出到 Console，
// 讓 host 建置階段本身（含下方讀取設定失敗的情況）也有日誌可查（Serilog.AspNetCore 既定慣例，
// 見 observability design.md 決策 1 與 Risk：初始化失敗時 fallback 到 Console 並記錄錯誤後才拋出）。
Log.Logger = SerilogConfigurator.CreateBootstrapLogger();

try
{
    Log.Information("Starting up");

    var builder = WebApplication.CreateBuilder(args);

    // Seq sink 獨立用 "Seq:ServerUrl" 這個純字串設定值判斷是否啟用，不透過 appsettings.json 的
    // Serilog:WriteTo 陣列宣告——陣列索引型的環境變數覆寫語法（例如 Serilog__WriteTo__1__Args__serverUrl）
    // 依 Serilog.Settings.Configuration 版本而定、脆弱且難以確認，改用單一純量設定值＋程式碼判斷是否
    // 有值再呼叫 WriteTo.Seq(...) 更直接可靠（observability design.md 決策 1 附錄的實作修正）。
    // 設定邏輯抽到 SerilogConfigurator，讓整合測試能透過 DI 註冊一個 ILogEventSink 供其掛上，
    // 不必修改這裡、也不必二次呼叫 UseSerilog（observability tasks.md 4.1，見該檔案內的說明）。
    // preserveStaticLogger: true——不要重新指定process 全域的 Log.Logger 靜態欄位：xUnit 平行執行
    // 多個測試類別時，每個類別各自的 WebApplicationFactory<Program> 都會重新跑一次這段 Program.Main
    // 邏輯，若都去改寫同一個靜態欄位會互搶、造成 ReloadableLogger 的 Freeze() 丟出「already frozen」
    // 例外（實測發現）。ILogger<T> 透過 DI 解析、不受這個參數影響，只有 Log.Logger 這個全域捷徑不受影響
    // ——啟動階段的 bootstrap logger（見上方 CreateBootstrapLogger）在 preserveStaticLogger: true 下會
    // 持續作為 Log.Logger，下方 catch 區塊的 Log.Fatal 因此穩定不受這裡的設定影響，是可接受的行為。
    builder.Host.UseSerilog(
        SerilogConfigurator.Configure,
        preserveStaticLogger: true);

    // 全域本文上限在 Kestrel 層設定，涵蓋所有端點（含匿名端點）；個別端點以 [RequestSizeLimit] 覆寫
    // （request-body-size-limits design.md 決策 2）。
    builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = RequestBodyLimits.GlobalBytes);

    // K8s 收到終止訊號時先跑 preStop sleep 5 秒，再給行程 terminationGracePeriodSeconds(30) 的剩餘時間；
    // .NET 預設 ShutdownTimeout 也是 30 秒，會在 SIGKILL 之前走不完，故設 20 秒讓背景服務與 Serilog 能乾淨收尾。
    builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(20));

    // Add services to the container.

    builder.Services.AddControllers();
    builder.Services.AddValidationProblemResponses();
    // Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
    builder.Services.AddOpenApi(options => options.AddDocumentTransformer<BearerSecuritySchemeTransformer>());

    builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
    builder.Services.AddProblemDetails();

    builder.Services.AddPersistence(builder.Configuration);
    builder.Services.AddAuthenticationAndAuthorization(builder.Configuration);
    builder.Services.AddTicketingServices(builder.Configuration);
    builder.Services.AddRedisServices(builder.Configuration);
    builder.Services.AddCaptcha(builder.Configuration);
    builder.Services.AddRateLimiting(builder.Configuration);
    builder.Services.AddClientAddressResolution();
    builder.Services.AddDevelopmentSeeding(builder.Configuration);
    builder.Services.AddAdminBootstrap(builder.Configuration);
    builder.Services.AddApplicationHandlers();
    builder.Services.AddBackgroundServices(builder.Environment);
    builder.Services.AddApiHealthChecks();

    var app = builder.Build();

    app.ResolveEagerlyValidatedOptions();

    // 展示用 seeder 只在 Development 執行（整合測試用 Testing 環境、正式環境皆不執行）；
    // 其餘環境改由 ProductionAdminBootstrapper 在資料庫無 Admin 且有設定 AdminBootstrap 時建立首位 Admin
    // （未設定則自行略過），兩套機制不在同一環境重疊（production-admin-bootstrap design.md 決策 2）。
    if (app.Environment.IsDevelopment())
    {
        using var seedScope = app.Services.CreateScope();
        await seedScope.ServiceProvider.GetRequiredService<DevelopmentDataSeeder>().SeedAsync(CancellationToken.None);
    }
    else
    {
        using var bootstrapScope = app.Services.CreateScope();
        await bootstrapScope.ServiceProvider.GetRequiredService<ProductionAdminBootstrapper>().BootstrapAsync(CancellationToken.None);
    }

    // Configure the HTTP request pipeline.
    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();
        app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1.json", "ProjectC API v1"));
    }

    // TraceId middleware 掛在最外層，讓包含 UseSerilogRequestLogging 摘要日誌在內的所有後續日誌
    // 都帶上同一個 TraceId（observability design.md 決策 2）。
    app.UseMiddleware<TraceIdLoggingMiddleware>();
    // UseSerilogRequestLogging() 預設走 Serilog.Log.Logger 這個process 全域靜態欄位，不是透過 DI
    // 解析——但上面 UseSerilog(..., preserveStaticLogger: true) 刻意不重新指定這個靜態欄位（避免
    // 測試併發下的 race condition），若不明確指定 options.Logger，請求摘要日誌會悄悄地只走 bootstrap
    // logger（只有 Console，沒有 Seq、沒有 appsettings 的等級設定），實測發現、非文件假設。
    // 明確指定為 DI 解析出來的 Serilog.ILogger（UseSerilog 一律會註冊，不受 preserveStaticLogger
    // 影響），才是這次 host 真正建置出來的那個 Logger。
    app.UseSerilogRequestLogging(options =>
    {
        options.Logger = app.Services.GetRequiredService<Serilog.ILogger>();
        options.GetLevel = ProbeRequestLogLevel.GetLevel;
    });

    app.UseExceptionHandler();

    // 還原須早於 HTTPS 轉址（讀 Scheme）與 UseRateLimiter（讀 RemoteIpAddress）；TraceId 與請求日誌在更外層，
    // 記錄到的仍是代理位址（reverse-proxy-forwarded-headers design.md 決策 2）。
    app.UseForwardedHeaders();

    // 平台探針以 HTTP 直連，被轉址後探測結果就不再反映實際狀態（api-health-checks design.md 決策 6）。
    app.UseWhen(
        context => !HealthCheckPaths.IsProbePath(context.Request.Path),
        branch => branch.UseHttpsRedirection());

    app.UseAuthentication();
    app.UseAuthorization();
    app.UseRateLimiter();

    app.MapControllers();

    // 明確停用限流：不依賴「目前沒有 GlobalLimiter」這個現況（api-health-checks design.md 決策 8）。
    app.MapHealthChecks(HealthCheckPaths.Live, new HealthCheckOptions { Predicate = _ => false })
        .AllowAnonymous()
        .DisableRateLimiting();
    app.MapHealthChecks(HealthCheckPaths.Ready, new HealthCheckOptions { Predicate = registration => registration.Tags.Contains(HealthCheckPaths.ReadyTag) })
        .AllowAnonymous()
        .DisableRateLimiting();

    app.Run();
}
catch (Exception exception)
{
    Log.Fatal(exception, "Application terminated unexpectedly");
    throw;
}
finally
{
    Log.CloseAndFlush();
}

[System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S1118", Justification = "整合測試的 WebApplicationFactory<Program> 需要此型別宣告。")]
public partial class Program;
