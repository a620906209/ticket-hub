using FluentAssertions;
using ProjectC.Infrastructure.Notifications;

namespace ProjectC.Infrastructure.Tests.Notifications;

public class EmailMaskerTests
{
    [Fact]
    public void Mask_WithOrdinaryEmail_ReturnsFirstCharacterPlusMaskAndDomain()
    {
        EmailMasker.Mask("buyer@example.com").Should().Be("b***@example.com");
    }

    [Fact]
    public void Mask_WithSingleCharacterLocalPart_ReturnsSameFormatWithoutThrowing()
    {
        EmailMasker.Mask("a@example.com").Should().Be("a***@example.com");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-email")]
    public void Mask_WithNullOrEmptyOrWhitespaceOrMissingAtSign_ReturnsRedactedWithoutThrowing(string? email)
    {
        EmailMasker.Mask(email).Should().Be("[redacted]");
    }

    [Theory]
    [InlineData("a@")]
    [InlineData("@example.com")]
    [InlineData("a@@example.com")]
    public void Mask_WithEmptyOrDuplicateAtSignBoundary_ReturnsRedactedWithoutThrowingOrLeakingPartialFormat(string email)
    {
        EmailMasker.Mask(email).Should().Be("[redacted]");
    }

    [Theory]
    [InlineData("a@ ")]
    [InlineData(" @example.com")]
    public void Mask_WithWhitespaceOnlyLocalOrDomainPart_ReturnsRedactedWithoutThrowing(string email)
    {
        EmailMasker.Mask(email).Should().Be("[redacted]");
    }
}
