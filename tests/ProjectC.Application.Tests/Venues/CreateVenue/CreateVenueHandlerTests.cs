using FluentAssertions;
using ProjectC.Application.Common;
using ProjectC.Application.Tests.TestSupport;
using ProjectC.Application.Venues.CreateVenue;

namespace ProjectC.Application.Tests.Venues.CreateVenue;

public class CreateVenueHandlerTests
{
    private readonly FakeVenueRepository _venueRepository = new();
    private readonly FakeUnitOfWork _unitOfWork = new();
    private readonly CreateVenueHandler _handler;

    public CreateVenueHandlerTests()
    {
        _handler = new CreateVenueHandler(_venueRepository, _unitOfWork, new CreateVenueRequestValidator());
    }

    [Fact]
    public async Task HandleAsync_WithValidName_CreatesVenue()
    {
        var request = new CreateVenueRequest("Taipei Arena");

        var result = await _handler.HandleAsync(request, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _venueRepository.Data.Should().ContainSingle(v => v.Name == "Taipei Arena" && v.Id == result.Value);
        _unitOfWork.LastTransaction!.Committed.Should().BeTrue();
    }

    [Fact]
    public async Task HandleAsync_WithBlankName_ReturnsValidationError()
    {
        var request = new CreateVenueRequest("  ");

        var result = await _handler.HandleAsync(request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Validation);
        _venueRepository.Data.Should().BeEmpty();
        _unitOfWork.BeginTransactionCallCount.Should().Be(0);
    }
}
