using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ProjectC.Domain.Authentication;
using ProjectC.Infrastructure.Persistence;

namespace ProjectC.Infrastructure.Tests.Authentication;

// time-field-naming-rule：EF model 的屬性名即欄位名（Configuration 未指定欄位名），不需要連資料庫。
public class TokenExpiryColumnMappingTests
{
    [Theory]
    [InlineData(typeof(RefreshToken))]
    [InlineData(typeof(PasswordResetToken))]
    public void Model_TokenEntity_MapsExpiryToExpiresAtUtc(Type tokenType)
    {
        using var dbContext = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql("Host=unused").Options);

        var entityType = dbContext.Model.FindEntityType(tokenType);

        entityType.Should().NotBeNull();
        entityType!.FindProperty("ExpiresAtUtc").Should().NotBeNull();
        entityType.FindProperty("ExpiresAt").Should().BeNull();
    }
}
