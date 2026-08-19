using FluentAssertions;
using ProjectC.Application.Tests.TestSupport;
using ProjectC.Application.Tickets.GetTicketTypes;
using ProjectC.Domain.Events;
using ProjectC.Domain.Venues;

namespace ProjectC.Application.Tests.Tickets.GetTicketTypes;

public class GetTicketTypesHandlerTests
{
    private readonly FakeEventRepository _eventRepository = new();
    private readonly FakeTicketTypeRepository _ticketTypeRepository = new();
    private readonly GetTicketTypesHandler _handler;

    public GetTicketTypesHandlerTests()
    {
        _handler = new GetTicketTypesHandler(_eventRepository, _ticketTypeRepository);
    }

    [Fact]
    public async Task HandleAsync_ReturnsRequiresSeatAndAvailableQuantityForBothModes()
    {
        var seatMap = new SeatMap(Guid.NewGuid(), Guid.NewGuid());
        seatMap.AddSeat("A", "1");
        var @event = new Event(Guid.NewGuid(), "Concert", DateTime.UtcNow.AddDays(1), Guid.NewGuid(), seatMap.Id);
        _eventRepository.Data.Add(@event);

        var seatTicketType = @event.CreateTicketType("A", 500m, seatMap);
        var countTicketType = @event.CreateCountBasedTicketType("站票", 300m, 50);
        _ticketTypeRepository.Data.Add(seatTicketType);
        _ticketTypeRepository.Data.Add(countTicketType);

        var result = await _handler.HandleAsync(@event.Id, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().ContainSingle(t => t.Id == seatTicketType.Id && t.RequiresSeat && t.AvailableQuantity == null);
        result.Value.Should().ContainSingle(t => t.Id == countTicketType.Id && !t.RequiresSeat && t.AvailableQuantity == 50);
    }
}
