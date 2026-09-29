using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using ProjectC.Application.Common;
using ProjectC.Application.Events.GetAdminEvents;
using ProjectC.Application.Organizers.SwitchOrganizerContext;
using ProjectC.Domain.Venues;
using ProjectC.Infrastructure.Persistence;
using ProjectC.WebApi.Tests.TestSupport;

namespace ProjectC.WebApi.Tests.Organizers;

/// <summary>
/// [EVT-MIGRATE-004]（event-management-organizer-scoping tasks.md 7.12）：驗證 Migration Plan「回填完成後，既有
/// Admin 只需呼叫一次切換即可恢復操作」這項部署保證，涵蓋整條「回填 → 切換 → 實際操作既有資料」路徑。
/// 獨立測試類別、獨立 factory：測試會把這個 factory 專屬的測試資料庫退回本次遷移之前的狀態再重新套用，
/// 不能和其他測試共用資料庫。
/// </summary>
public class EventOrganizerBackfillEndToEndTests : IClassFixture<CustomWebApplicationFactory>
{
    private const string MigrationBeforeScoping = "20260922142257_AddOrganizers";
    private static readonly Guid LegacyOrganizerId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly CustomWebApplicationFactory _factory;

    public EventOrganizerBackfillEndToEndTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task ExistingAdmin_AfterBackfillMigrations_CanSwitchToLegacyOrganizerAndSeeBackfilledEvent()
    {
        var email = AuthTestHelper.NewEmail();
        await AuthTestHelper.RegisterAsync(_factory.CreateClient(), email);
        await AuthTestHelper.PromoteToAdminAsync(_factory.Services, email);
        Guid legacyEventId;
        using (var scope = _factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var migrator = dbContext.GetService<IMigrator>();
            await migrator.MigrateAsync(MigrationBeforeScoping);
            legacyEventId = await SeedLegacyEventAsync(dbContext);

            await migrator.MigrateAsync();
        }

        var tokens = await AuthTestHelper.LoginAsync(_factory.CreateClient(), email);
        var switchClient = _factory.CreateClient();
        switchClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        var switchResponse = await switchClient.PostAsJsonAsync(
            $"/api/organizers/{LegacyOrganizerId}/switch-context", new SwitchOrganizerContextRequest(tokens.RefreshToken));

        switchResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var switchedAccessToken = (await switchResponse.Content.ReadFromJsonAsync<SwitchOrganizerContextResultDto>())!.AccessToken;
        new JwtSecurityTokenHandler().ReadJwtToken(switchedAccessToken).Claims
            .Should().ContainSingle(c => c.Type == CustomClaimTypes.OrganizerId).Which.Value.Should().Be(LegacyOrganizerId.ToString());

        var adminClient = _factory.CreateClient();
        adminClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", switchedAccessToken);
        var events = await adminClient.GetFromJsonAsync<List<AdminEventSummaryDto>>("/api/admin/events");
        events.Should().ContainSingle(e => e.Id == legacyEventId);
    }

    /// <summary>遷移前的 schema 沒有 Events.OrganizerId，目前的 EF model 卻把它視為必填，因此既有 Event 只能以
    /// 參數化原生 SQL 寫入；Venue／SeatMap 不受本次遷移影響，照常透過 EF Core 寫入。</summary>
    private static async Task<Guid> SeedLegacyEventAsync(ApplicationDbContext dbContext)
    {
        var venue = new Venue(Guid.NewGuid(), "Legacy Venue");
        var seatMap = new SeatMap(Guid.NewGuid(), venue.Id);
        dbContext.Venues.Add(venue);
        dbContext.SeatMaps.Add(seatMap);
        await dbContext.SaveChangesAsync();

        var eventId = Guid.NewGuid();
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Events" ("Id", "Title", "StartAtUtc", "VenueId", "SeatMapId", "IsQueueModeEnabled")
            VALUES ({eventId}, 'Legacy Event', {DateTime.UtcNow.AddDays(30)}, {venue.Id}, {seatMap.Id}, false)
            """);
        return eventId;
    }
}
