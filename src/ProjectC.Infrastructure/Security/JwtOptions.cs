using System.ComponentModel.DataAnnotations;

namespace ProjectC.Infrastructure.Security;

public class JwtOptions
{
    public const string SectionName = "Jwt";

    [Required]
    public string Issuer { get; set; } = string.Empty;

    [Required]
    public string Audience { get; set; } = string.Empty;

    [Required]
    [MinLength(32, ErrorMessage = "JWT SigningKey 長度至少須為 32 個字元，以確保簽章強度。")]
    public string SigningKey { get; set; } = string.Empty;

    [Range(1, 1440)]
    public int AccessTokenExpirationMinutes { get; set; } = 30;
}
