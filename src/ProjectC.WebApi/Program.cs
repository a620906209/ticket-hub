using System.Text;
using FluentValidation;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
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
using ProjectC.Domain.Orders;
using ProjectC.Domain.Payments;
using ProjectC.Domain.Tickets;
using ProjectC.Domain.Venues;
using ProjectC.Infrastructure.Payments;
using ProjectC.Infrastructure.Persistence;
using ProjectC.Infrastructure.Persistence.Repositories;
using ProjectC.Infrastructure.Security;
using ProjectC.Infrastructure.Tickets;
using ProjectC.WebApi.BackgroundServices;
using ProjectC.WebApi.Common;
using ProjectC.WebApi.ExceptionHandling;
using ProjectC.WebApi.OpenApi;

var builder = WebApplication.CreateBuilder(args);

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
builder.Services.AddScoped<GetOrderByIdHandler>();
builder.Services.AddScoped<GetMyOrdersHandler>();
builder.Services.AddScoped<GetMyOrderDetailHandler>();
builder.Services.AddScoped<GetTicketQrCodeHandler>();
builder.Services.AddScoped<RedeemTicketHandler>();

// Testing 環境（見 CustomWebApplicationFactory.UseEnvironment("Testing")）不啟動真實背景服務，
// 否則所有既有 WebApi 整合測試都會連帶啟動一個對著自己 Testcontainers 資料庫跑的清理服務
// （見 ticketing-order-management design.md 決策 5）。
if (!builder.Environment.IsEnvironment("Testing"))
{
    builder.Services.AddHostedService<ExpiredOrderCleanupService>();
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

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1.json", "ProjectC API v1"));
}

app.UseExceptionHandler();

app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();

public partial class Program;
