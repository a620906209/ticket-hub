using FluentAssertions;
using ProjectC.Application.Common.Paging;

namespace ProjectC.Application.Tests.Common;

// list-pagination LP-RESP-001：回應必須同時帶頁資料、實際套用的 page/pageSize 與不受分頁影響的總筆數。
public class PagedResultDtoTests
{
    [Fact]
    public void Constructor_StoresItemsPageSizeAndTotalCount()
    {
        var result = new PagedResultDto<string>(["a", "b"], 2, 20, 45);

        result.Items.Should().Equal("a", "b");
        result.Page.Should().Be(2);
        result.PageSize.Should().Be(20);
        result.TotalCount.Should().Be(45);
    }
}
