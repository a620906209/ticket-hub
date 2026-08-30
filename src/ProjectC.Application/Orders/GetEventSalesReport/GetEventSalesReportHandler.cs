using ProjectC.Application.Common;
using ProjectC.Domain.Events;
using ProjectC.Domain.Orders;
using ProjectC.Domain.Tickets;

namespace ProjectC.Application.Orders.GetEventSalesReport;

public sealed class GetEventSalesReportHandler
{
    private readonly IEventRepository _eventRepository;
    private readonly ITicketTypeRepository _ticketTypeRepository;
    private readonly IOrderRepository _orderRepository;

    public GetEventSalesReportHandler(
        IEventRepository eventRepository,
        ITicketTypeRepository ticketTypeRepository,
        IOrderRepository orderRepository)
    {
        _eventRepository = eventRepository;
        _ticketTypeRepository = ticketTypeRepository;
        _orderRepository = orderRepository;
    }

    public async Task<Result<SalesReportDto>> HandleAsync(Guid eventId, CancellationToken cancellationToken)
    {
        var @event = await _eventRepository.GetByIdAsync(eventId, cancellationToken);
        if (@event is null)
        {
            return Result<SalesReportDto>.Failure(Error.NotFound($"Event '{eventId}' was not found."));
        }

        var ticketTypes = await _ticketTypeRepository.GetByEventIdAsync(eventId, cancellationToken);
        var groups = await _orderRepository.GetPaidItemSalesByEventIdAsync(eventId, cancellationToken);

        // 一個分組算不算「屬於本活動」，依它的 TicketTypeId 是否存在於本活動票種目錄清單中判斷
        // （design.md 決策 2、3）：不在清單中的分組（TicketTypeId 為 null，或有值但屬於別的活動）一律
        // 併入「無法歸類」，不得靜默捨棄。
        var groupsByTicketTypeId = groups
            .Where(g => g.TicketTypeId.HasValue)
            .ToDictionary(g => g.TicketTypeId!.Value);

        var byTicketType = ticketTypes
            .Select(t => groupsByTicketTypeId.TryGetValue(t.Id, out var group)
                ? new TicketTypeSalesDto(t.Id, t.ZoneCode, t.RequiresSeat, group.QuantitySold, group.Revenue)
                : new TicketTypeSalesDto(t.Id, t.ZoneCode, t.RequiresSeat, QuantitySold: 0, Revenue: 0m))
            .ToList();

        var ticketTypeIdsInEvent = ticketTypes.Select(t => t.Id).ToHashSet();
        var unclassifiedGroups = groups
            .Where(g => !g.TicketTypeId.HasValue || !ticketTypeIdsInEvent.Contains(g.TicketTypeId.Value))
            .ToList();

        var dto = new SalesReportDto(
            @event.Id,
            @event.Title,
            TotalRevenue: groups.Sum(g => g.Revenue),
            TotalTicketsSold: groups.Sum(g => g.QuantitySold),
            byTicketType,
            UnclassifiedItemCount: unclassifiedGroups.Sum(g => g.ItemCount),
            UnclassifiedTicketsSold: unclassifiedGroups.Sum(g => g.QuantitySold),
            UnclassifiedRevenue: unclassifiedGroups.Sum(g => g.Revenue));

        return Result<SalesReportDto>.Success(dto);
    }
}
