using FluentAssertions;
using FluentValidation.Results;
using ProjectC.Application.Common;
using ProjectC.Application.Common.Validation;

namespace ProjectC.Application.Tests.Common;

// validation-error-fields：驗證失敗須保留「哪個欄位出錯」，client 才能把訊息對應到欄位；
// detail（Message）則須與引進 errors 前逐字相同，舊 client 不受影響。
public class ValidationResultExtensionsTests
{
    private static ValidationResult Fail(params (string Property, string Message)[] failures) =>
        new(failures.Select(f => new ValidationFailure(f.Property, f.Message)).ToList());

    // [AER-019]
    [Fact]
    public void ToValidationError_WithSingleFailingField_ReturnsFieldErrorsWithThatKey()
    {
        var error = Fail(("Title", "too long")).ToValidationError();

        error.Type.Should().Be(ErrorType.Validation);
        error.FieldErrors.Should().NotBeNull();
        error.FieldErrors!.Keys.Should().BeEquivalentTo(["Title"]);
        error.FieldErrors["Title"].Should().Equal("too long");
    }

    // [AER-020]
    [Fact]
    public void ToValidationError_WithMultipleFailingFields_ReturnsOneKeyPerField()
    {
        var error = Fail(("Title", "t1"), ("VenueId", "v1")).ToValidationError();

        error.FieldErrors!.Keys.Should().BeEquivalentTo(["Title", "VenueId"]);
        error.FieldErrors["Title"].Should().Equal("t1");
        error.FieldErrors["VenueId"].Should().Equal("v1");
    }

    // [AER-021]
    [Fact]
    public void ToValidationError_WithMultipleRulesOnSameField_AggregatesAllMessages()
    {
        var error = Fail(("Title", "m1"), ("Title", "m2")).ToValidationError();

        error.FieldErrors!["Title"].Should().Equal("m1", "m2");
    }

    // [AER-022] 欄位分組會讓交錯訊息重新排序，所以 Message 必須維持回報順序串接（含交錯情境）。
    [Fact]
    public void ToValidationError_Message_EqualsLegacySpaceJoinedErrorMessages()
    {
        var result = Fail(("A", "m1"), ("B", "m2"), ("A", "m3"));

        var error = result.ToValidationError();

        error.Message.Should().Be(string.Join(" ", result.Errors.Select(e => e.ErrorMessage)));
        error.Message.Should().Be("m1 m2 m3");
        error.FieldErrors!["A"].Should().Equal("m1", "m3");
    }

    // [AER-023] 特性固定測試：單參數多載是業務規則錯誤的入口，必須維持沒有欄位語意。
    [Fact]
    public void Validation_WithoutFieldErrors_HasNullFieldErrors()
    {
        Error.Validation("business rule").FieldErrors.Should().BeNull();
    }
}
