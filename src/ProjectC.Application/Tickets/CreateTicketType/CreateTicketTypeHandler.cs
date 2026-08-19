using FluentValidation;
using ProjectC.Application.Common;
using ProjectC.Application.Common.Interfaces;
using ProjectC.Domain.Events;
using ProjectC.Domain.Tickets;
using ProjectC.Domain.Venues;

namespace ProjectC.Application.Tickets.CreateTicketType;

public sealed class CreateTicketTypeHandler
{
    private readonly IEventRepository _eventRepository;
    private readonly ISeatMapRepository _seatMapRepository;
    private readonly ITicketTypeRepository _ticketTypeRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IValidator<CreateTicketTypeRequest> _validator;

    public CreateTicketTypeHandler(
        IEventRepository eventRepository,
        ISeatMapRepository seatMapRepository,
        ITicketTypeRepository ticketTypeRepository,
        IUnitOfWork unitOfWork,
        IValidator<CreateTicketTypeRequest> validator)
    {
        _eventRepository = eventRepository;
        _seatMapRepository = seatMapRepository;
        _ticketTypeRepository = ticketTypeRepository;
        _unitOfWork = unitOfWork;
        _validator = validator;
    }

    public async Task<Result<Guid>> HandleAsync(Guid eventId, CreateTicketTypeRequest request, CancellationToken cancellationToken)
    {
        var validation = await _validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return Result<Guid>.Failure(Error.Validation(string.Join(" ", validation.Errors.Select(e => e.ErrorMessage))));
        }

        var @event = await _eventRepository.GetByIdAsync(eventId, cancellationToken);
        if (@event is null)
        {
            return Result<Guid>.Failure(Error.NotFound($"Event '{eventId}' was not found."));
        }

        TicketType ticketType;
        if (request.RequiresSeat)
        {
            // 理論上一定查得到：Events.SeatMapId 有 FK 約束指向 SeatMaps，這次也沒有任何 Delete 端點。
            // 仍防禦性處理，避免對 null 解參考（見 design.md 決策 3）。
            var seatMap = await _seatMapRepository.GetByIdAsync(@event.SeatMapId, cancellationToken);
            if (seatMap is null)
            {
                return Result<Guid>.Failure(Error.NotFound("找不到活動對應的座位圖。"));
            }

            var zoneExists = seatMap.Seats.Any(s => s.ZoneCode == request.ZoneCode);
            if (!zoneExists)
            {
                return Result<Guid>.Failure(Error.Validation($"Zone '{request.ZoneCode}' does not exist in the event's seat map."));
            }

            try
            {
                ticketType = @event.CreateTicketType(request.ZoneCode, request.Price, seatMap);
            }
            catch (ArgumentOutOfRangeException)
            {
                // 最後防線：正常流程不會走到這裡，票價已經被 FluentValidation 擋過。
                return Result<Guid>.Failure(Error.Validation("Ticket price must be greater than zero."));
            }
            catch (InvalidOperationException)
            {
                // 最後防線：正常流程不會走到這裡，分區存在性已經在上面檢查過。
                // 如果還是被觸發，代表預先檢查邏輯本身有漏洞（見 design.md 決策 2）。
                return Result<Guid>.Failure(Error.Validation($"Zone '{request.ZoneCode}' does not exist in the event's seat map."));
            }
        }
        else
        {
            // 純計數模式：不驗證/不需要座位圖，AvailableQuantity 已經被 FluentValidation 保證是正整數。
            try
            {
                ticketType = @event.CreateCountBasedTicketType(request.ZoneCode, request.Price, request.AvailableQuantity!.Value);
            }
            catch (ArgumentOutOfRangeException)
            {
                // 最後防線：正常流程不會走到這裡，票價/可售總量已經被 FluentValidation 擋過。
                return Result<Guid>.Failure(Error.Validation("Ticket price and available quantity must be greater than zero."));
            }
        }

        await using var transaction = await _unitOfWork.BeginTransactionAsync(cancellationToken);
        _ticketTypeRepository.Add(ticketType);
        await transaction.CommitAsync(cancellationToken);

        return Result<Guid>.Success(ticketType.Id);
    }
}
