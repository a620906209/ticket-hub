namespace ProjectC.Domain.Tickets;

public interface ITicketQrCodeGenerator
{
    byte[] GeneratePng(Guid ticketId);
}
