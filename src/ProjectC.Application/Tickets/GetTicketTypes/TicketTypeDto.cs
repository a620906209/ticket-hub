namespace ProjectC.Application.Tickets.GetTicketTypes;

public sealed record TicketTypeDto(Guid Id, string ZoneCode, decimal Price, bool RequiresSeat, int? AvailableQuantity);
