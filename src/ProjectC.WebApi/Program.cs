using System.Text;
using System.Threading.RateLimiting;
using FluentValidation;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using StackExchange.Redis;
using ProjectC.Application.Authentication.Login;
using ProjectC.Application.Authentication.Logout;
using ProjectC.Application.Authentication.PasswordReset;
using ProjectC.Application.Authentication.Refresh;
using ProjectC.Application.Common;
using ProjectC.Application.Common.Interfaces;
using ProjectC.Application.Events.CreateEvent;
using ProjectC.Application.Events.GetAdminEvents;
using ProjectC.Application.Events.GetEventSeats;
using ProjectC.Application.Events.GetEvents;
using ProjectC.Application.Events.SetEventQueueMode;
using ProjectC.Application.Members.Activate;
using ProjectC.Application.Members.Deactivate;
using ProjectC.Application.Members.GetMyProfile;
using ProjectC.Application.Members.Register;
using ProjectC.Application.Members.UpdateMyProfile;
using ProjectC.Application.Orders;
using ProjectC.Application.Orders.GetOrderById;
using ProjectC.Application.Orders.GetMyOrderDetail;
using ProjectC.Application.Orders.GetMyOrders;
using ProjectC.Application.Orders.GetOrders;
using ProjectC.Application.Orders.GetEventSalesReport;
using ProjectC.Application.PurchaseQueue.GetMyQueueStatus;
using ProjectC.Application.PurchaseQueue.JoinPurchaseQueue;
using ProjectC.Application.Tickets.CreateTicketType;
using ProjectC.Application.Tickets.GetTicketQrCode;
using ProjectC.Application.Tickets.GetTicketTypes;
using ProjectC.Application.Tickets.RedeemTicket;
using ProjectC.Application.Venues.CreateSeatMap;
using ProjectC.Application.Venues.CreateVenue;
using ProjectC.Application.Venues.GetSeatMapById;
using ProjectC.Application.Venues.GetVenueById;
using ProjectC.Application.Venues.GetVenues;
using ProjectC.Domain.Events;
using ProjectC.Domain.Notifications;
using ProjectC.Domain.Orders;
using ProjectC.Domain.Payments;
using ProjectC.Domain.PurchaseQueue;
using ProjectC.Domain.Tickets;
using ProjectC.Domain.Venues;
using ProjectC.Infrastructure.DistributedLocking;
using ProjectC.Infrastructure.Notifications;
using ProjectC.Infrastructure.Payments;
using ProjectC.Infrastructure.Persistence;
using ProjectC.Infrastructure.Persistence.Repositories;
using ProjectC.Infrastructure.Security;
using ProjectC.Infrastructure.Tickets;
using ProjectC.WebApi.BackgroundServices;
using ProjectC.WebApi.Common;
using ProjectC.WebApi.ExceptionHandling;
using ProjectC.WebApi.Logging;
using ProjectC.WebApi.OpenApi;

// Bootstrap logger：在 appsettings.json 的 Serilog 設定節被讀取之前先能輸出到 Console，
// 讓 host 建置階段本身（含下方讀取設定失敗的情況）也有日誌可查（Serilog.AspNetCore 既定慣例，
// 見 observability design.md 決策 1 與 Risk：初始化失敗時 fallback 到 Console 並記錄錯誤後才拋出）。
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

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

    // Add services to the container.

    builder.Services.AddControllers();
    // Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
    builder.Services.AddOpenApi(options => options.AddDocumentTransformer<BearerSecuritySchemeTransformer>());

    builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
    builder.Services.AddProblemDetails();

    builder.Services.AddDbContext<ApplicationDbContext>(options =>
        options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));
    builder.Services.AddScoped<IApplicationDbContext>(sp => sp.GetRequiredService<ApplicationDbContext>());

    // Scoped：都綁定同一個 DbContext 的生命週期（一次 HTTP request／一個測試範圍一個 instance）。
    builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();
    builder.Services.AddScoped<IVenueRepository, VenueRepository>();
    builder.Services.AddScoped<ISeatMapRepository, SeatMapRepository>();
    builder.Services.AddScoped<IEventRepository, EventRepository>();
    builder.Services.AddScoped<IEventSeatRepository, EventSeatRepository>();
    builder.Services.AddScoped<ITicketTypeRepository, TicketTypeRepository>();
    builder.Services.AddScoped<IOrderRepository, OrderRepository>();
    builder.Services.AddScoped<ITicketRepository, TicketRepository>();
    builder.Services.AddScoped<IPurchaseQueueRepository, PurchaseQueueRepository>();

    // JwtOptions：啟動時驗證，SigningKey 等缺失直接讓應用程式啟動失敗（Fail Fast，見 design.md 決策 9）。
    builder.Services
        .AddOptions<JwtOptions>()
        .Bind(builder.Configuration.GetSection(JwtOptions.SectionName))
        .ValidateDataAnnotations()
        .ValidateOnStart();

    builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection("Auth"));
    builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<AuthOptions>>().Value);

    // OrderCleanupOptions 有安全的預設值，不像 JwtOptions 缺值就無法運作，不需要 ValidateOnStart（見 design.md 決策 2）。
    builder.Services.Configure<OrderCleanupOptions>(builder.Configuration.GetSection("OrderCleanup"));
    builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<OrderCleanupOptions>>().Value);

    // MockPaymentGatewayOptions 有安全的預設值（AlwaysSucceed = true），比照 OrderCleanupOptions 不需要 ValidateOnStart
    // （見 order-payment-gateway-alignment design.md 決策 2）。
    builder.Services.Configure<MockPaymentGatewayOptions>(builder.Configuration.GetSection(MockPaymentGatewayOptions.SectionName));
    builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<MockPaymentGatewayOptions>>().Value);
    builder.Services.AddSingleton<IPaymentGateway, MockPaymentGateway>();

    // MockEmailNotificationServiceOptions 有安全的預設值（AlwaysSucceed = true），比照 MockPaymentGatewayOptions
    // 不需要 ValidateOnStart（見 email-notification design.md 決策 4）。
    builder.Services.Configure<MockEmailNotificationServiceOptions>(builder.Configuration.GetSection(MockEmailNotificationServiceOptions.SectionName));
    builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<MockEmailNotificationServiceOptions>>().Value);
    builder.Services.AddSingleton<IEmailNotificationService, MockEmailNotificationService>();

    // TicketSigningOptions：比照 JwtOptions 啟動時 fail-fast 驗證，簽章金鑰缺失或過弱直接讓應用程式啟動失敗
    // （見 design.md 決策 3）；再比照 AuthOptions/OrderCleanupOptions/MockPaymentGatewayOptions 解包成
    // 一般 class 註冊為 Singleton，讓 HmacTicketSigningService 能直接建構子注入。
    builder.Services
        .AddOptions<TicketSigningOptions>()
        .Bind(builder.Configuration.GetSection(TicketSigningOptions.SectionName))
        .ValidateDataAnnotations()
        .ValidateOnStart();
    builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<TicketSigningOptions>>().Value);

    // HmacTicketSigningService 無狀態，比照 MockPaymentGateway 的 Singleton 選擇
    // （純運算、thread-safe，不持有任何 DbContext 或 request-scoped 狀態）。
    builder.Services.AddSingleton<ITicketSigningService, HmacTicketSigningService>();
    builder.Services.AddTransient<ITicketQrCodeGenerator, TicketQrCodeGenerator>();

    // RateLimitingOptions 有安全的預設值，不像 PurchaseQueueOptions 缺值就無法運作，不需要 ValidateOnStart；
    // 只能用 AddOptions<T>().Bind(...).ValidateDataAnnotations()——Configure<T>() 回傳 IServiceCollection，
    // 無法直接串接 ValidateDataAnnotations()（見 rate-limiting-queue design.md 決策 1）。
    builder.Services
        .AddOptions<RateLimitingOptions>()
        .Bind(builder.Configuration.GetSection(RateLimitingOptions.SectionName))
        .ValidateDataAnnotations();
    builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<RateLimitingOptions>>().Value);

    // LoginRateLimitingOptions：與 RateLimitingOptions 分開的獨立設定類別，分區鍵語意不同（來源 IP vs
    // 已登入會員 Id），數值也刻意設得更嚴格（login-rate-limiting design.md 決策 2）；同樣有安全預設值，
    // 不需要 ValidateOnStart，寫法逐行比照上面的 RateLimitingOptions。
    builder.Services
        .AddOptions<LoginRateLimitingOptions>()
        .Bind(builder.Configuration.GetSection(LoginRateLimitingOptions.SectionName))
        .ValidateDataAnnotations();
    builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<LoginRateLimitingOptions>>().Value);

    // PurchaseQueueOptions：任一值缺漏或為 0／負數都會讓熱門搶購模式的活動功能完全失效，
    // 比照 JwtOptions/TicketSigningOptions 啟動時 fail-fast（見 design.md 決策 3）。
    builder.Services
        .AddOptions<PurchaseQueueOptions>()
        .Bind(builder.Configuration.GetSection(PurchaseQueueOptions.SectionName))
        .ValidateDataAnnotations()
        .ValidateOnStart();
    builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<PurchaseQueueOptions>>().Value);

    // DistributedLockOptions 有安全的預設值（LockTtlMultiplier = 3），比照 RateLimitingOptions
    // 不需要 ValidateOnStart（見 purchase-queue-leader-election design.md Migration Plan）。
    builder.Services
        .AddOptions<DistributedLockOptions>()
        .Bind(builder.Configuration.GetSection(DistributedLockOptions.SectionName))
        .ValidateDataAnnotations();
    builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<DistributedLockOptions>>().Value);

    // AbortOnConnectFail MUST 為 false：StackExchange.Redis 預設值為 true，Redis 尚未就緒時
    // ConnectionMultiplexer.Connect(...) 會直接拋出例外並阻塞應用程式啟動，違反 fail-open 的
    // 降級原則（purchase-queue-leader-election design.md 決策 4／spec.md PQLE-010）。
    // 連線字串 MUST 在 factory delegate 內部才讀取 builder.Configuration（而非在這裡提前算好
    // ConfigurationOptions 再以 closure 帶入），否則會讀到 builder.Build() 之前的設定快照——
    // WebApplicationFactory 測試用的設定覆寫是在 Build() 當下才併入 builder.Configuration，
    // 提前讀取會讀到覆寫前的舊值（實測發現：PQLE-010 的啟動測試曾因此意外連到正式 redis:6379，
    // 而非測試指定的不可達 endpoint）。空字串會讓 ConfigurationOptions.Parse 產出零個 EndPoint，
    // ConnectionMultiplexer.Connect(...) 會直接同步拋出「沒有指定任何 endpoint」的例外——這與
    // AbortOnConnectFail 無關（後者只處理「endpoint 已指定但連不上」），MUST 確保永遠有一個
    // 語法合法的 endpoint 可供解析。
    builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    {
        var redisConfigurationOptions = ConfigurationOptions.Parse(builder.Configuration.GetConnectionString("Redis") ?? "redis:6379");
        redisConfigurationOptions.AbortOnConnectFail = false;
        return ConnectionMultiplexer.Connect(redisConfigurationOptions);
    });
    // IConnectionMultiplexer 官方建議整個應用程式共用單一實例，本身即是 thread-safe，比照既有
    // Singleton 註冊慣例（如 IMemoryCache），IDistributedLock 一併註冊為 Singleton。
    builder.Services.AddSingleton<IDistributedLock, RedisDistributedLock>();

    builder.Services.AddSingleton<IDateTimeProvider, SystemDateTimeProvider>();
    builder.Services.AddTransient<IPasswordHasher, BCryptPasswordHasher>();
    builder.Services.AddTransient<ITokenService, JwtTokenService>();

    builder.Services.AddValidatorsFromAssemblyContaining<RegisterMemberRequestValidator>();

    builder.Services.AddScoped<CreateVenueHandler>();
    builder.Services.AddScoped<CreateSeatMapHandler>();
    builder.Services.AddScoped<GetVenuesHandler>();
    builder.Services.AddScoped<GetVenueByIdHandler>();
    builder.Services.AddScoped<GetSeatMapByIdHandler>();
    builder.Services.AddScoped<CreateEventHandler>();
    builder.Services.AddScoped<CreateTicketTypeHandler>();
    builder.Services.AddScoped<GetAdminEventsHandler>();

    builder.Services.AddScoped<CreateOrderHandler>();
    builder.Services.AddScoped<ConfirmOrderHandler>();
    builder.Services.AddScoped<CancelOrderHandler>();
    builder.Services.AddScoped<OrderService>();
    builder.Services.AddScoped<GetEventsHandler>();
    builder.Services.AddScoped<GetEventSeatsHandler>();
    builder.Services.AddScoped<GetTicketTypesHandler>();
    builder.Services.AddScoped<GetOrdersHandler>();
    builder.Services.AddScoped<GetEventSalesReportHandler>();
    builder.Services.AddScoped<GetOrderByIdHandler>();
    builder.Services.AddScoped<GetMyOrdersHandler>();
    builder.Services.AddScoped<GetMyOrderDetailHandler>();
    builder.Services.AddScoped<GetTicketQrCodeHandler>();
    builder.Services.AddScoped<RedeemTicketHandler>();
    builder.Services.AddScoped<SetEventQueueModeHandler>();
    builder.Services.AddScoped<JoinPurchaseQueueHandler>();
    builder.Services.AddScoped<GetMyQueueStatusHandler>();

    // Testing 環境（見 CustomWebApplicationFactory.UseEnvironment("Testing")）不啟動真實背景服務，
    // 否則所有既有 WebApi 整合測試都會連帶啟動一個對著自己 Testcontainers 資料庫跑的清理服務
    // （見 ticketing-order-management design.md 決策 5）。
    if (!builder.Environment.IsEnvironment("Testing"))
    {
        builder.Services.AddHostedService<ExpiredOrderCleanupService>();
        builder.Services.AddHostedService<PurchaseQueueAdmissionService>();
    }

    builder.Services.AddScoped<RegisterMemberHandler>();
    builder.Services.AddScoped<GetMyProfileHandler>();
    builder.Services.AddScoped<UpdateMyProfileHandler>();
    builder.Services.AddScoped<ActivateMemberHandler>();
    builder.Services.AddScoped<DeactivateMemberHandler>();
    builder.Services.AddScoped<LoginHandler>();
    builder.Services.AddScoped<RefreshTokenHandler>();
    builder.Services.AddScoped<LogoutHandler>();
    builder.Services.AddScoped<RequestPasswordResetHandler>();
    builder.Services.AddScoped<ResetPasswordHandler>();

    builder.Services
        .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.MapInboundClaims = false;

            var jwtSection = builder.Configuration.GetSection(JwtOptions.SectionName);
            var jwtOptions = jwtSection.Get<JwtOptions>() ?? new JwtOptions();

            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = jwtOptions.Issuer,
                ValidateAudience = true,
                ValidAudience = jwtOptions.Audience,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.SigningKey)),
                ClockSkew = TimeSpan.FromSeconds(30),
            };
        });

    builder.Services.AddAuthorizationBuilder()
        .AddPolicy(AuthorizationPolicies.AdminOnly, policy => policy.RequireRole("Admin"));

    // place-order/confirm-order 是分區鍵為會員 Id 的獨立命名 Fixed Window policy，各自累計、不共用計數
    // （rate-limiting-queue design.md 決策 1）；login 呼叫當下使用者尚未通過驗證，改以來源 IP 分區
    // （login-rate-limiting design.md 決策 1）。三個 policy 各自獨立計數，皆共用下方同一個 OnRejected。
    builder.Services.AddRateLimiter(rateLimiterOptions =>
    {
        rateLimiterOptions.AddPolicy("place-order", httpContext => CreateMemberPartition(httpContext));
        rateLimiterOptions.AddPolicy("confirm-order", httpContext => CreateMemberPartition(httpContext));
        rateLimiterOptions.AddPolicy("login", httpContext => CreateIpPartition(httpContext));

        rateLimiterOptions.OnRejected = async (context, cancellationToken) =>
        {
            int? retryAfterSeconds = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
                ? (int)retryAfter.TotalSeconds
                : null;

            if (retryAfterSeconds is { } seconds)
            {
                context.HttpContext.Response.Headers["Retry-After"] = seconds.ToString();
            }

            var problemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status429TooManyRequests,
                Title = "TooManyRequests",
                Extensions = { ["traceId"] = context.HttpContext.TraceIdentifier },
            };

            context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            await context.HttpContext.Response.WriteAsJsonAsync(problemDetails, cancellationToken);
        };

        return;

        static RateLimitPartition<string> CreateMemberPartition(HttpContext httpContext)
        {
            var options = httpContext.RequestServices.GetRequiredService<RateLimitingOptions>();
            var partitionKey = httpContext.User.GetMemberId().ToString();

            return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = options.PermitLimit,
                Window = TimeSpan.FromSeconds(options.WindowSeconds),
                QueueLimit = 0,
            });
        }

        // 登入端點呼叫當下使用者尚未通過驗證，沒有會員 Id 可用，改以來源 IP 分區
        // （login-rate-limiting design.md 決策 1、3）。
        static RateLimitPartition<string> CreateIpPartition(HttpContext httpContext)
        {
            var options = httpContext.RequestServices.GetRequiredService<LoginRateLimitingOptions>();
            var partitionKey = LoginRateLimiterPartitioning.GetPartitionKey(httpContext);

            return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = options.PermitLimit,
                Window = TimeSpan.FromSeconds(options.WindowSeconds),
                QueueLimit = 0,
            });
        }
    });

    var app = builder.Build();

    // RateLimitingOptions 沒有 ValidateOnStart()，這裡強制在啟動時觸發一次 IOptions<T>.Value 解析，
    // 讓 DataAnnotations 驗證確實在應用程式啟動過程中執行，不延遲到第一個進入端點的 HTTP 請求
    // （rate-limiting-queue design.md 決策 1）。MUST 解析 unwrap 後的 RateLimitingOptions（而不只是
    // IOptions<RateLimitingOptions> 這個 wrapper 本身）——只取得 wrapper 不會觸發 .Value 存取，
    // 驗證就不會真的執行；line 126 註冊的 Singleton 工廠內部才會呼叫 .Value。
    app.Services.GetRequiredService<RateLimitingOptions>();

    // LoginRateLimitingOptions 同樣沒有 ValidateOnStart()，理由與上面的 RateLimitingOptions 相同
    // （login-rate-limiting design.md 決策 3）。
    app.Services.GetRequiredService<LoginRateLimitingOptions>();

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
    app.UseSerilogRequestLogging(options => options.Logger = app.Services.GetRequiredService<Serilog.ILogger>());

    app.UseExceptionHandler();

    app.UseHttpsRedirection();

    app.UseAuthentication();
    app.UseAuthorization();
    app.UseRateLimiter();

    app.MapControllers();

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

public partial class Program;
