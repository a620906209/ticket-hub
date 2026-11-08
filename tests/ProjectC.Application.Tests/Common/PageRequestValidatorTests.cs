using FluentAssertions;
using ProjectC.Application.Common;
using ProjectC.Application.Common.Paging;
using ProjectC.Application.Common.Validation;

namespace ProjectC.Application.Tests.Common;

// list-pagination：分頁參數越界必須被拒絕而非靜默截斷，否則端點可被當成整表匯出（LP-VALID-*）。
public class PageRequestValidatorTests
{
    private readonly PageRequestValidator _validator = new();

    // [LP-PARAM-001]
    [Fact]
    public void Constants_AreTheDocumentedPagingConvention()
    {
        PageRequest.DefaultPage.Should().Be(1);
        PageRequest.DefaultPageSize.Should().Be(20);
        PageRequest.MaxPageSize.Should().Be(100);
        PageRequest.MaxPage.Should().Be(10000);
    }

    // [LP-VALID-001]
    [Fact]
    public void Validate_WhenPageSizeExceedsMax_FailsOnPageSize()
    {
        var result = _validator.Validate(new PageRequest(1, 101));

        result.IsValid.Should().BeFalse();
        result.ToValidationError().FieldErrors!.Keys.Should().BeEquivalentTo(["PageSize"]);
    }

    // [LP-VALID-002]
    [Fact]
    public void Validate_WhenPageSizeEqualsMax_Passes()
        => _validator.Validate(new PageRequest(1, 100)).IsValid.Should().BeTrue();

    // [LP-VALID-006]
    [Theory]
    [InlineData(10001)]
    [InlineData(int.MaxValue)]
    public void Validate_WhenPageExceedsMax_FailsOnPage(int page)
    {
        var result = _validator.Validate(new PageRequest(page, 100));

        result.IsValid.Should().BeFalse();
        result.ToValidationError().FieldErrors!.Keys.Should().BeEquivalentTo(["Page"]);
    }

    // [LP-VALID-007]
    [Fact]
    public void Validate_WhenPageEqualsMax_Passes()
        => _validator.Validate(new PageRequest(10000, 100)).IsValid.Should().BeTrue();

    // [LP-VALID-003]
    [Theory]
    [InlineData(0, 20, "Page")]
    [InlineData(-1, 20, "Page")]
    [InlineData(1, 0, "PageSize")]
    public void Validate_WhenBelowMinimum_FailsOnThatField(int page, int pageSize, string expectedField)
    {
        var result = _validator.Validate(new PageRequest(page, pageSize));

        result.IsValid.Should().BeFalse();
        result.ToValidationError().FieldErrors!.Keys.Should().BeEquivalentTo([expectedField]);
    }

    // [LP-VALID-005]
    [Fact]
    public void Validate_WhenBothOutOfRange_ReportsBothFields()
    {
        var error = _validator.Validate(new PageRequest(0, 101)).ToValidationError();

        error.Type.Should().Be(ErrorType.Validation);
        error.FieldErrors!.Keys.Should().BeEquivalentTo(["Page", "PageSize"]);
    }

    [Fact]
    public void Validate_WhenDefaults_Passes()
        => _validator.Validate(new PageRequest(PageRequest.DefaultPage, PageRequest.DefaultPageSize)).IsValid.Should().BeTrue();
}
