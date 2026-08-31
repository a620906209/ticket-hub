using FluentAssertions;
using ProjectC.Infrastructure.Tickets;

namespace ProjectC.Infrastructure.Tests.Tickets;

public class HmacTicketSigningServiceTests
{
    private static HmacTicketSigningService CreateService()
        => new(new TicketSigningOptions { SigningKey = "unit-test-ticket-signing-key-not-for-prod-32+" });

    [Fact]
    public void TryVerify_WhenContentSignedByThisService_ReturnsTrueAndRestoresTicketId()
    {
        var service = CreateService();
        var ticketId = Guid.NewGuid();
        var content = service.Sign(ticketId);

        var verified = service.TryVerify(content, out var restoredTicketId);

        verified.Should().BeTrue();
        restoredTicketId.Should().Be(ticketId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void TryVerify_WhenContentIsNullOrEmpty_ReturnsFalseWithoutThrowing(string? content)
    {
        var service = CreateService();

        var verified = service.TryVerify(content, out var ticketId);

        verified.Should().BeFalse();
        ticketId.Should().Be(Guid.Empty);
    }

    [Fact]
    public void TryVerify_WhenContentTamperedByOneCharacter_ReturnsFalse()
    {
        var service = CreateService();
        var content = service.Sign(Guid.NewGuid());
        var tampered = TamperLastCharacter(content);

        var verified = service.TryVerify(tampered, out _);

        verified.Should().BeFalse();
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("no-separator-here")]
    [InlineData("3fa85f64-5717-4562-b3fc-2c963f66afa6.sig.extra")]
    [InlineData("not-a-guid.someSignature")]
    [InlineData("3fa85f64-5717-4562-b3fc-2c963f66afa6.")]
    public void TryVerify_WhenContentIsMalformed_ReturnsFalseWithoutThrowing(string content)
    {
        var service = CreateService();

        var verified = service.TryVerify(content, out var ticketId);

        verified.Should().BeFalse();
        ticketId.Should().Be(Guid.Empty);
    }

    private static string TamperLastCharacter(string content)
    {
        var lastChar = content[^1];
        var replacement = lastChar == 'a' ? 'b' : 'a';
        return content[..^1] + replacement;
    }
}
