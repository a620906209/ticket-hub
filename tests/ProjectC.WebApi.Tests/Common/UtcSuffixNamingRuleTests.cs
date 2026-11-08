using System.Reflection;
using FluentAssertions;
using ProjectC.Application.Common.Interfaces;
using ProjectC.Domain.Authentication;
using ProjectC.WebApi.Common;

namespace ProjectC.WebApi.Tests.Common;

// time-field-naming-rule：DateTime 時間欄位一律以 Utc 結尾，新增違規欄位時此測試立刻失敗。
public class UtcSuffixNamingRuleTests
{
    // 例外以「型別全名＋屬性名」精確比對，不得用萬用字元；新增例外須寫明理由。
    private static readonly HashSet<string> Exceptions =
    [
        // .NET 慣用名 UtcNow，名稱已標明 UTC，改名會牽動全專案。
        $"{typeof(IDateTimeProvider).FullName}.{nameof(IDateTimeProvider.UtcNow)}",
    ];

    // 掃描範圍太小（例如組件載入錯誤）會讓規則空過；下限取自實測掃描數。
    private const int MinimumExpectedDateTimePropertyCount = 32;

    [Fact]
    public void DateTimeProperties_AcrossAssemblies_EndWithUtc()
    {
        Assembly[] assemblies =
        [
            typeof(RefreshToken).Assembly,
            typeof(IDateTimeProvider).Assembly,
            typeof(FieldErrorKeyFormatter).Assembly,
        ];

        var dateTimeProperties = assemblies
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type.IsVisible)
            .SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(property => property.PropertyType == typeof(DateTime) || property.PropertyType == typeof(DateTime?))
            .ToList();

        dateTimeProperties.Count.Should().BeGreaterThanOrEqualTo(MinimumExpectedDateTimePropertyCount);

        var violations = dateTimeProperties
            .Where(property => !property.Name.EndsWith("Utc", StringComparison.Ordinal))
            .Select(property => $"{property.DeclaringType!.FullName}.{property.Name}")
            .Where(fullName => !Exceptions.Contains(fullName))
            .OrderBy(fullName => fullName, StringComparer.Ordinal)
            .ToList();

        violations.Should().BeEmpty("以下 DateTime 屬性未以 Utc 結尾：{0}", string.Join(", ", violations));
    }
}
