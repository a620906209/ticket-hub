using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using ProjectC.Infrastructure.Tickets;

namespace ProjectC.WebApi.Tests.Startup;

public class JwtOptionsFailFastTests
{
    [Fact]
    public void CreatingHost_WithoutJwtSigningKey_ThrowsOptionsValidationException()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configBuilder) =>
            {
                configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Jwt:Issuer"] = "",
                    ["Jwt:Audience"] = "",
                    ["Jwt:SigningKey"] = "",
                    // TicketSigningOptions 也有 ValidateOnStart（見 Program.cs），這裡補上合法值讓它通過驗證，
                    // 避免這支只測 Jwt fail-fast 的測試因為另一個無關的 Options 同時驗證失敗，
                    // 讓宿主丟出包住兩個 OptionsValidationException 的 AggregateException 造成斷言歧義。
                    // 長度依 TicketSigningOptions.SigningKey 的 [MinLength(32)] 規則；key/長度都源自該類別本身，
                    // 避免這裡的字面值跟它的驗證規則各自漂移。
                    [$"{TicketSigningOptions.SectionName}:{nameof(TicketSigningOptions.SigningKey)}"] = new string('x', 32),
                });
            });
        });

        var act = () => factory.Server;

        act.Should().Throw<Exception>()
            .Where(e => e is OptionsValidationException || ContainsOptionsValidationException(e));
    }

    private static bool ContainsOptionsValidationException(Exception exception)
    {
        var current = exception;
        while (current is not null)
        {
            if (current is OptionsValidationException)
            {
                return true;
            }

            current = current.InnerException;
        }

        return false;
    }
}
