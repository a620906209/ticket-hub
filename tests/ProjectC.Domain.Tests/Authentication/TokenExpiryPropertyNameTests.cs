using FluentAssertions;
using ProjectC.Domain.Authentication;

namespace ProjectC.Domain.Tests.Authentication;

// time-field-naming-rule：以反射檢查屬性名，避免靠編譯失敗當 RED。
public class TokenExpiryPropertyNameTests
{
    [Theory]
    [InlineData(typeof(RefreshToken))]
    [InlineData(typeof(PasswordResetToken))]
    public void ExpiryProperty_OnTokenEntity_IsNamedExpiresAtUtc(Type tokenType)
    {
        tokenType.GetProperty("ExpiresAtUtc").Should().NotBeNull();
        tokenType.GetProperty("ExpiresAt").Should().BeNull();
    }
}
