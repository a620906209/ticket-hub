using FluentAssertions;
using ProjectC.Application.Tests.TestSupport;
using ProjectC.Application.Venues.GetVenues;
using ProjectC.Domain.Venues;

namespace ProjectC.Application.Tests.Venues.GetVenues;

public class GetVenuesHandlerTests
{
    private readonly FakeVenueRepository _venueRepository = new();
    private readonly GetVenuesHandler _handler;

    public GetVenuesHandlerTests()
    {
        _handler = new GetVenuesHandler(_venueRepository);
    }

    [Fact]
    public async Task HandleAsync_WithVenues_ReturnsSortedByName()
    {
        var venueB = new Venue(Guid.NewGuid(), "B Venue");
        var venueA = new Venue(Guid.NewGuid(), "A Venue");
        _venueRepository.Data.Add(venueB);
        _venueRepository.Data.Add(venueA);

        var result = await _handler.HandleAsync(CancellationToken.None);

        result.Should().BeEquivalentTo(
            [new VenueSummaryDto(venueA.Id, "A Venue"), new VenueSummaryDto(venueB.Id, "B Venue")],
            options => options.WithStrictOrdering());
    }

    [Fact]
    public async Task HandleAsync_WithNoVenues_ReturnsEmptyList()
    {
        var result = await _handler.HandleAsync(CancellationToken.None);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_WithDuplicateNames_OrdersByIdAsTieBreaker()
    {
        var first = new Venue(Guid.Parse("00000000-0000-0000-0000-000000000001"), "Same Name");
        var second = new Venue(Guid.Parse("00000000-0000-0000-0000-000000000002"), "Same Name");
        _venueRepository.Data.Add(second);
        _venueRepository.Data.Add(first);

        var result = await _handler.HandleAsync(CancellationToken.None);

        result.Should().BeEquivalentTo(
            [new VenueSummaryDto(first.Id, "Same Name"), new VenueSummaryDto(second.Id, "Same Name")],
            options => options.WithStrictOrdering());
    }
}
