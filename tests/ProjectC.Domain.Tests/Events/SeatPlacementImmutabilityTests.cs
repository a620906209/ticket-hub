using System.Reflection;
using FluentAssertions;
using ProjectC.Domain.Events;
using ProjectC.Domain.Venues;

namespace ProjectC.Domain.Tests.Events;

/// <summary>
/// TP-ORDER-031：下單把座位圖成員與分區比對移到交易前、以未加鎖的讀取判斷（order-placement-p95-optimization
/// design.md 決策 4），正確性依賴這些欄位建構後不可變。日後若有人加上 setter／init，或改成可被方法修改的自訂欄位，
/// 這裡會失敗，提醒須把比對移回鎖內。
/// </summary>
public class SeatPlacementImmutabilityTests
{
    [Theory]
    [InlineData(typeof(Seat), nameof(Seat.SeatMapId))]
    [InlineData(typeof(Seat), nameof(Seat.ZoneCode))]
    [InlineData(typeof(EventSeat), nameof(EventSeat.SeatId))]
    [InlineData(typeof(EventSeat), nameof(EventSeat.EventId))]
    [InlineData(typeof(Event), nameof(Event.SeatMapId))]
    public void Property_WhenUsedByPreTransactionSeatChecks_IsGetterOnlyAutoProperty(Type type, string propertyName)
    {
        AssertGetterOnlyAutoProperty(type, propertyName);
    }

    [Fact]
    public void AssertGetterOnlyAutoProperty_WhenPropertyReadsMutableField_Fails()
    {
        // 反向對照：沒有 setter、但讀取可被方法修改的欄位，必須被判定為違反不變量，否則上面的 Theory 形同虛設。
        var act = () => AssertGetterOnlyAutoProperty(typeof(MutableThroughMethod), nameof(MutableThroughMethod.SeatMapId));

        act.Should().Throw<Exception>().WithMessage("*getter-only 自動屬性*", "必須是 backing field 檢查擋下，而不是找不到屬性等其他原因");
    }

    private static void AssertGetterOnlyAutoProperty(Type type, string propertyName)
    {
        var property = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);

        property.Should().NotBeNull();
        // init accessor 也是 SetMethod（帶 IsExternalInit modreq），所以只要 SetMethod 為 null 就同時排除 setter 與 init。
        property!.SetMethod.Should().BeNull($"{type.Name}.{propertyName} 必須建構後不可變");

        // 只檢查 setter 擋不住「改成 `=> _field` 再加一個會改 `_field` 的方法」。要求維持 getter-only 自動屬性
        // （編譯器產生的 readonly backing field），改成任何自訂欄位都會失敗、迫使重新檢視此不變量。
        var backingField = type.GetField($"<{propertyName}>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
        backingField.Should().NotBeNull($"{type.Name}.{propertyName} 必須是 getter-only 自動屬性");
        backingField!.IsInitOnly.Should().BeTrue($"{type.Name}.{propertyName} 的 backing field 必須是 readonly");
    }

    private sealed class MutableThroughMethod
    {
        private Guid _seatMapId;

        public Guid SeatMapId => _seatMapId;

        public void MoveToSeatMap(Guid seatMapId) => _seatMapId = seatMapId;
    }
}
