using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace ProjectC.WebApi.Tests.Startup;

public class TicketSigningOptionsFailFastTests
{
    [Fact]
    public void CreatingHost_WithoutTicketSigningKey_ThrowsOptionsValidationException()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configBuilder) =>
            {
                configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Jwt:Issuer"] = "ProjectC.Tests",
                    ["Jwt:Audience"] = "ProjectC.Tests.Client",
                    ["Jwt:SigningKey"] = "startup-test-jwt-signing-key-not-for-prod-32chars+",
                    ["TicketSigning:SigningKey"] = "",
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
