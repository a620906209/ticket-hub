using FluentAssertions;
using ProjectC.Application.Common;
using ProjectC.Application.Tests.TestSupport;
using ProjectC.Application.Tickets.CreateTicketType;
using ProjectC.Domain.Events;
using ProjectC.Domain.Venues;

namespace ProjectC.Application.Tests.Tickets.CreateTicketType;

public class CreateTicketTypeHandlerTests
{
    private readonly FakeEventRepository _eventRepository = new();
    private readonly FakeSeatMapRepository _seatMapRepository = new();
    private readonly FakeTicketTypeRepository _ticketTypeRepository = new();
    private readonly FakeUnitOfWork _unitOfWork = new();
    private readonly CreateTicketTypeHandler _handler;

    public CreateTicketTypeHandlerTests()
    {
        _handler = new CreateTicketTypeHandler(
            _eventRepository, _seatMapRepository, _ticketTypeRepository, _unitOfWork, new CreateTicketTypeRequestValidator());
    }

    private Guid SeedEventWithZone(string zoneCode)
    {
        var venueId = Guid.NewGuid();
        var seatMap = new SeatMap(Guid.NewGuid(), venueId);
        seatMap.AddSeat(zoneCode, "1");
        _seatMapRepository.Data.Add(seatMap);

        var @event = new Event(Guid.NewGuid(), "Concert", DateTime.UtcNow.AddDays(30), venueId, seatMap.Id);
        _eventRepository.Data.Add(@event);

        return @event.Id;
    }

    [Fact]
    public async Task HandleAsync_WithExistingZoneAndValidPrice_CreatesTicketType()
    {
        var eventId = SeedEventWithZone("A");
        var request = new CreateTicketTypeRequest("A", 500m);

        var result = await _handler.HandleAsync(eventId, request, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _ticketTypeRepository.Data.Should().ContainSingle(t => t.Id == result.Value && t.Price == 500m);
    }

    [Fact]
    public async Task HandleAsync_WithZeroPrice_ReturnsValidationError()
    {
        var eventId = SeedEventWithZone("A");
        var request = new CreateTicketTypeRequest("A", 0m);

        var result = await _handler.HandleAsync(eventId, request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Validation);
        _ticketTypeRepository.Data.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_WithZoneNotInSeatMap_ReturnsValidationError()
    {
        var eventId = SeedEventWithZone("A");
        var request = new CreateTicketTypeRequest("B", 500m);

        var result = await _handler.HandleAsync(eventId, request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Validation);
        _ticketTypeRepository.Data.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_WithNonExistentEvent_ReturnsNotFound()
    {
        var request = new CreateTicketTypeRequest("A", 500m);

        var result = await _handler.HandleAsync(Guid.NewGuid(), request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.NotFound);
        _ticketTypeRepository.Data.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_WithRequiresSeatFalseAndPositiveQuantity_CreatesCountBasedTicketType()
    {
        var eventId = SeedEventWithZone("A");
        var request = new CreateTicketTypeRequest("站票", 500m, RequiresSeat: false, AvailableQuantity: 100);

        var result = await _handler.HandleAsync(eventId, request, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var ticketType = _ticketTypeRepository.Data.Single(t => t.Id == result.Value);
        ticketType.RequiresSeat.Should().BeFalse();
        ticketType.AvailableQuantity.Should().Be(100);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task HandleAsync_WithRequiresSeatFalseAndInvalidQuantity_ReturnsValidationError(int? quantity)
    {
        var eventId = SeedEventWithZone("A");
        var request = new CreateTicketTypeRequest("站票", 500m, RequiresSeat: false, AvailableQuantity: quantity);

        var result = await _handler.HandleAsync(eventId, request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Validation);
        _ticketTypeRepository.Data.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_WithRequiresSeatTrueAndAvailableQuantityProvided_ReturnsValidationError()
    {
        var eventId = SeedEventWithZone("A");
        var request = new CreateTicketTypeRequest("A", 500m, RequiresSeat: true, AvailableQuantity: 10);

        var result = await _handler.HandleAsync(eventId, request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Validation);
        _ticketTypeRepository.Data.Should().BeEmpty();
    }
}
