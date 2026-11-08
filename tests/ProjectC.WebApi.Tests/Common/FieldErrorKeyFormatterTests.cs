using FluentAssertions;
using ProjectC.WebApi.Common;

namespace ProjectC.WebApi.Tests.Common;

// validation-error-fields：Handler 路徑與模型繫結路徑共用同一份鍵格式化，鍵規則才不會漂移。
public class FieldErrorKeyFormatterTests
{
    // [AER-024]
    [Fact]
    public void Format_NestedCollectionPath_LowercasesEachSegmentAndKeepsIndex()
    {
        FieldErrorKeyFormatter.Format("Selections[0].Quantity").Should().Be("selections[0].quantity");
    }

    // [AER-025]
    [Fact]
    public void Format_PascalCaseName_LowercasesOnlyFirstCharacter()
    {
        FieldErrorKeyFormatter.Format("StartAtUtc").Should().Be("startAtUtc");
    }

    [Fact]
    public void Format_JsonPathPrefix_StripsDollarDot()
    {
        FieldErrorKeyFormatter.Format("$.maxTicketsPerOrder").Should().Be("maxTicketsPerOrder");
        FieldErrorKeyFormatter.Format("$.Selections[1].Quantity").Should().Be("selections[1].quantity");
    }

    [Theory]
    [InlineData("$")]
    [InlineData("")]
    public void Format_RootKey_ReturnsEmptyString(string key)
    {
        FieldErrorKeyFormatter.Format(key).Should().BeEmpty();
    }

    [Theory]
    [InlineData("$['x y']", "['x y']")]
    [InlineData("$['a.b']", "['a.b']")]
    [InlineData("$.selections[0]['a b']", "selections[0]['a b']")]
    [InlineData("$.Selections[0]['A.b'].Quantity", "selections[0]['A.b'].quantity")]
    public void Format_BracketNotationPath_StripsRootAndKeepsBracketSegmentVerbatim(string key, string expected)
    {
        FieldErrorKeyFormatter.Format(key).Should().Be(expected);
    }

    [Fact]
    public void FormatAll_KeysCollidingAfterConversion_MergesMessages()
    {
        var formatted = FieldErrorKeyFormatter.FormatAll(
        [
            new KeyValuePair<string, string[]>("Title", ["m1"]),
            new KeyValuePair<string, string[]>("title", ["m2", "m3"]),
        ]);

        formatted.Keys.Should().BeEquivalentTo(["title"]);
        formatted["title"].Should().Equal("m1", "m2", "m3");
    }
}
