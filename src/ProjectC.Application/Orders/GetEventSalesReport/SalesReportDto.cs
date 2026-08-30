namespace ProjectC.Application.Orders.GetEventSalesReport;

public sealed record SalesReportDto(
    Guid EventId,
    string EventTitle,
    decimal TotalRevenue,
    int TotalTicketsSold,
    IReadOnlyList<TicketTypeSalesDto> ByTicketType,
    int UnclassifiedItemCount,
    int UnclassifiedTicketsSold,
    decimal UnclassifiedRevenue);

public sealed record TicketTypeSalesDto(Guid TicketTypeId, string ZoneCode, bool RequiresSeat, int QuantitySold, decimal Revenue);
