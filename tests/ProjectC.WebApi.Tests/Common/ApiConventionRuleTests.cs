using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using ProjectC.Application.Events.GetEvents;
using ProjectC.WebApi.Controllers;

namespace ProjectC.WebApi.Tests.Common;

// docs/api-conventions.md 的跨模組慣例：路由命名、列表分頁。違反時此測試立刻失敗，不靠 reviewer 記得對照。
public class ApiConventionRuleTests
{
    // 路由例外以完整樣板精確比對，不得用萬用字元；新增例外須寫明理由，並同步 api-conventions.md 第 3 節。
    private static readonly HashSet<string> RouteTemplateExceptions =
    [
        // 認證與驗證碼是動作型而非資源型端點，單數命名。
        "api/auth",
        "api/captcha",
        // 子資源以活動為前綴（api-conventions.md 第 3 節「路由」已列為例外）。
        "api/events/{id:guid}/queue",
    ];

    // 回傳集合的 Handler 必須分頁（PageRequest／PagedResultDto），除非資料量天然有界並列於此。
    // 例外以「Handler 型別全名」精確比對；理由見 api-conventions.md 第 3 節「列表分頁」。
    private static readonly HashSet<string> UnpagedCollectionHandlerExceptions =
    [
        // 少量固定資料。
        "ProjectC.Application.Venues.GetVenues.GetVenuesHandler",
        // 受限於單一會員的主辦方數。
        "ProjectC.Application.Organizers.GetMyOrganizers.GetMyOrganizersHandler",
        // 全平台待審主辦方清單，規模可接受，列為觀察項目，成長時另案分頁。
        "ProjectC.Application.Organizers.GetPendingOrganizers.GetPendingOrganizersHandler",
        // 單一活動內的座位與票種，由活動規模決定，非跨活動的無界列表。
        "ProjectC.Application.Events.GetEventSeats.GetEventSeatsHandler",
        "ProjectC.Application.Tickets.GetTicketTypes.GetTicketTypesHandler",
    ];

    // 掃描範圍太小（例如組件載入錯誤）會讓規則空過；下限取自實測掃描數。
    private const int MinimumExpectedControllerCount = 14;

    private static readonly Regex ResourceRoutePattern = new("^api/(admin/)?[a-z]+s$", RegexOptions.Compiled);

    [Fact]
    public void ControllerRoutes_AcrossWebApi_FollowLowercasePluralResourceNaming()
    {
        var controllers = typeof(EventsController).Assembly.GetTypes()
            .Where(type => !type.IsAbstract && typeof(ControllerBase).IsAssignableFrom(type))
            .ToList();

        controllers.Count.Should().BeGreaterThanOrEqualTo(MinimumExpectedControllerCount);

        var violations = controllers
            .Select(controller => (controller, template: controller.GetCustomAttribute<RouteAttribute>()?.Template))
            .Where(entry => entry.template is null
                || (!RouteTemplateExceptions.Contains(entry.template) && !ResourceRoutePattern.IsMatch(entry.template)))
            .Select(entry => $"{entry.controller.Name}: {entry.template ?? "(沒有 [Route])"}")
            .OrderBy(text => text, StringComparer.Ordinal)
            .ToList();

        violations.Should().BeEmpty("以下 Controller 的路由不符 api/{{小寫複數資源}}、api/admin/{{資源}}，也不在例外清單：{0}", string.Join("; ", violations));
    }

    [Fact]
    public void RouteTemplateExceptions_AllStillUsedByControllers()
    {
        var usedTemplates = typeof(EventsController).Assembly.GetTypes()
            .Select(type => type.GetCustomAttribute<RouteAttribute>()?.Template)
            .Where(template => template is not null)
            .ToHashSet();

        var stale = RouteTemplateExceptions.Where(template => !usedTemplates.Contains(template)).ToList();

        stale.Should().BeEmpty("以下例外已無 Controller 使用，請自例外清單移除：{0}", string.Join(", ", stale));
    }

    [Fact]
    public void HandlersReturningCollections_AcrossApplication_ArePagedOrListedAsBounded()
    {
        var handlers = typeof(GetEventsHandler).Assembly.GetTypes()
            .Where(type => type.IsClass && !type.IsAbstract && type.IsVisible && type.Name.EndsWith("Handler", StringComparison.Ordinal))
            .ToList();

        handlers.Count.Should().BeGreaterThanOrEqualTo(30);

        var collectionHandlers = handlers
            .Where(handler => handler.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Any(method => method.Name == "HandleAsync" && IsCollectionResult(method.ReturnType)))
            .Select(handler => handler.FullName!)
            .ToList();

        var unpaged = collectionHandlers.Where(name => !UnpagedCollectionHandlerExceptions.Contains(name)).OrderBy(name => name, StringComparer.Ordinal).ToList();
        unpaged.Should().BeEmpty("以下 Handler 回傳集合但未分頁，也不在有界資料例外清單（見 api-conventions.md 第 3 節）：{0}", string.Join(", ", unpaged));

        var stale = UnpagedCollectionHandlerExceptions.Where(name => !collectionHandlers.Contains(name)).ToList();
        stale.Should().BeEmpty("以下例外已不回傳集合，請自例外清單移除：{0}", string.Join(", ", stale));
    }

    // 拆掉 Task<> 與 Result<> 外層後，剩下的是否為集合型別（字串與 byte[] 不算）。
    private static bool IsCollectionResult(Type returnType)
    {
        var current = returnType;
        while (current.IsGenericType && (current.GetGenericTypeDefinition() == typeof(Task<>) || current.Name == "Result`1"))
        {
            current = current.GetGenericArguments()[0];
        }

        return current != typeof(string) && current != typeof(byte[])
            && (current.IsArray || (current.IsGenericType && current.GetInterfaces().Append(current)
                .Any(type => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))));
    }
}
