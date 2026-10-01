namespace ProjectC.Application.Orders.GetMyOrderDetail;

public sealed record MyOrderDetailDto(
    Guid Id,
    Guid EventId,
    string EventTitle,
    string Status,
    DateTime HeldUntilUtc,
    IReadOnlyList<MyOrderItemDto> Items);

/// <param name="SeatZoneCode">純計數項目（<paramref name="EventSeatId"/> 為 null）時與 <paramref name="SeatNumber"/> 同為 null，否則兩者皆有值。
/// 刻意加上 Seat 前綴，避免與同一筆項目的票種 ZoneCode 混淆。</param>
/// <param name="TicketTypeName">取自票種的 ZoneCode；<paramref name="TicketTypeId"/> 為 null 的舊訂單項目為 null。</param>
public sealed record MyOrderItemDto(
    Guid Id,
    Guid? EventSeatId,
    Guid? TicketTypeId,
    string? SeatZoneCode,
    string? SeatNumber,
    string? TicketTypeName,
    int Quantity,
    decimal UnitPrice,
    IReadOnlyList<MyTicketDto> Tickets);

public sealed record MyTicketDto(Guid Id, string Status);
