using System.ComponentModel.DataAnnotations;

namespace ProjectC.Infrastructure.Tickets;

public class TicketSigningOptions
{
    public const string SectionName = "TicketSigning";

    // HMAC-SHA256 建議金鑰長度至少等於雜湊輸出長度（256 bits = 32 bytes），比照 JwtOptions.SigningKey 的最小長度要求。
    [Required]
    [MinLength(32, ErrorMessage = "TicketSigning SigningKey 長度至少須為 32 個字元，以確保簽章強度。")]
    public string SigningKey { get; set; } = string.Empty;
}
