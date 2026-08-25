using ProjectC.Domain.Tickets;
using QRCoder;

namespace ProjectC.Infrastructure.Tickets;

// 按需（on-demand）產生：輸入 Ticket ID 才能推導出內容與圖檔，不持久化、不快取（design.md 決策 1、3）。
// 買家票券查詢端點按需產生 QR Code；現場掃碼前端仍待另開提案。
public sealed class TicketQrCodeGenerator : ITicketQrCodeGenerator
{
    private readonly ITicketSigningService _signingService;

    public TicketQrCodeGenerator(ITicketSigningService signingService)
    {
        _signingService = signingService;
    }

    // PngByteQRCode 是純受管理程式碼實作（不依賴 System.Drawing），才能在 Linux 容器內運作。
    public byte[] GeneratePng(Guid ticketId)
    {
        var content = _signingService.Sign(ticketId);

        using var qrGenerator = new QRCodeGenerator();
        using var qrCodeData = qrGenerator.CreateQrCode(content, QRCodeGenerator.ECCLevel.Q);
        var qrCode = new PngByteQRCode(qrCodeData);
        return qrCode.GetGraphic(20);
    }
}
