using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using ProjectC.Domain.Members;
using ProjectC.Infrastructure.Persistence;
using ProjectC.Infrastructure.Persistence.Repositories;
using ProjectC.WebApi.ExceptionHandling;

namespace ProjectC.WebApi.Tests.TestSupport;

// real-name-verification tasks.md 5.1（design.md 決策 2 ChangeTracker 探針）與 5.12（RDM-HOLDER-007）專用：
// 真實 DB 無法穩定重現「條件式 UPDATE 輸掉競爭」與「需實名活動的買家沒有實名」，改以包裝真實 Repository 的
// decorator 只對測試指定的 Member Id 注入故障；其餘呼叫一律委派給真實實作。
public sealed class RealNameFaultInjectionWebApplicationFactory : CustomWebApplicationFactory
{
    /// <summary>這些會員的 <c>TryRegisterAsync</c> 不寫入、直接回傳 false，模擬被並發請求搶先。</summary>
    public ConcurrentDictionary<Guid, byte> LostRaceMemberIds { get; } = new();

    /// <summary>這些會員的 <c>GetAsync</c> 回傳 null，模擬資料損毀。</summary>
    public ConcurrentDictionary<Guid, byte> MissingRealNameMemberIds { get; } = new();

    /// <summary>每個請求結束前（request scope 仍存活時）擷取該 scope 的 DbContext 中 Modified 狀態的 Member 數。</summary>
    public ConcurrentQueue<(string Path, int ModifiedMemberCount)> ChangeTrackerSnapshots { get; } = new();

    public RecordingLogger<GlobalExceptionHandler> ExceptionHandlerLogger { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IMemberRealNameRepository>();
            services.AddScoped<IMemberRealNameRepository>(sp => new FaultInjectingMemberRealNameRepository(
                new MemberRealNameRepository(sp.GetRequiredService<ApplicationDbContext>()), LostRaceMemberIds, MissingRealNameMemberIds));

            services.RemoveAll<ILogger<GlobalExceptionHandler>>();
            services.AddSingleton<ILogger<GlobalExceptionHandler>>(ExceptionHandlerLogger);

            services.AddSingleton<IStartupFilter>(new ChangeTrackerProbeStartupFilter(ChangeTrackerSnapshots));
        });
    }

    private sealed class FaultInjectingMemberRealNameRepository(
        IMemberRealNameRepository inner,
        ConcurrentDictionary<Guid, byte> lostRaceMemberIds,
        ConcurrentDictionary<Guid, byte> missingRealNameMemberIds) : IMemberRealNameRepository
    {
        public Task<bool> TryRegisterAsync(Guid memberId, string realName, string nationalIdLast4, CancellationToken cancellationToken)
            => lostRaceMemberIds.ContainsKey(memberId)
                ? Task.FromResult(false)
                : inner.TryRegisterAsync(memberId, realName, nationalIdLast4, cancellationToken);

        public Task<MemberRealName?> GetAsync(Guid memberId, CancellationToken cancellationToken)
            => missingRealNameMemberIds.ContainsKey(memberId)
                ? Task.FromResult<MemberRealName?>(null)
                : inner.GetAsync(memberId, cancellationToken);
    }

    private sealed class ChangeTrackerProbeStartupFilter(ConcurrentQueue<(string Path, int ModifiedMemberCount)> snapshots) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (HttpContext context, RequestDelegate nextMiddleware) =>
            {
                await nextMiddleware(context);
                var dbContext = context.RequestServices.GetRequiredService<ApplicationDbContext>();
                var modifiedMemberCount = dbContext.ChangeTracker.Entries<Member>().Count(e => e.State == EntityState.Modified);
                snapshots.Enqueue((context.Request.Path.Value ?? string.Empty, modifiedMemberCount));
            });
            next(app);
        };
    }
}
