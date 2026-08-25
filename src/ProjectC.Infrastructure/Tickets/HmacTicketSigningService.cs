using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using ProjectC.Domain.Tickets;

namespace ProjectC.Infrastructure.Tickets;

public sealed class HmacTicketSigningService : ITicketSigningService
{
    private const char Separator = '.';

    private readonly byte[] _keyBytes;

    public HmacTicketSigningService(TicketSigningOptions options)
    {
        _keyBytes = Encoding.UTF8.GetBytes(options.SigningKey);
    }

    public string Sign(Guid ticketId)
    {
        var idText = ticketId.ToString("D");
        return $"{idText}{Separator}{ComputeSignature(idText)}";
    }

    public bool TryVerify(string? content, out Guid ticketId)
    {
        ticketId = Guid.Empty;

        if (string.IsNullOrEmpty(content))
            return false;

        var separatorIndex = content.IndexOf(Separator);
        if (separatorIndex < 0)
            return false;

        var idText = content[..separatorIndex];
        var providedSignature = content[(separatorIndex + 1)..];

        if (!Guid.TryParseExact(idText, "D", out var parsedTicketId))
            return false;

        // 定長比對避免時序側通道洩漏簽章內容；長度不同時 FixedTimeEquals 直接回傳 false，不需要另外先比較長度。
        var expectedBytes = Encoding.UTF8.GetBytes(ComputeSignature(idText));
        var providedBytes = Encoding.UTF8.GetBytes(providedSignature);
        if (!CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes))
            return false;

        ticketId = parsedTicketId;
        return true;
    }

    private string ComputeSignature(string idText)
    {
        var messageBytes = Encoding.UTF8.GetBytes(idText);
        var hash = HMACSHA256.HashData(_keyBytes, messageBytes);
        return Base64UrlEncoder.Encode(hash);
    }
}
