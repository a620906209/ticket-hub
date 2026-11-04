using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using ProjectC.WebApi.Logging;

namespace ProjectC.WebApi.Tests.Logging;

public class SensitiveTextMaskerTests
{
    private const string Secret = "s3cret-pw";

    // 對應 OBS-EXMASK-005、017、019、025
    [Theory]
    [InlineData("Pool=true;Pwd=s3cret-pw;Port=5432", "Pool=true;Pwd=***;Port=5432")]
    [InlineData("Server=db;Password=s3cret-pw;Port=5432", "Server=db;Password=***;Port=5432")]
    [InlineData("Server=db;Passwd=s3cret-pw;Port=5432", "Server=db;Passwd=***;Port=5432")]
    [InlineData("Host=db;PASSWORD='s3cret-pw';Port=5432", "Host=db;PASSWORD='***';Port=5432")]
    [InlineData("Host=db;Password=\"s3cret-pw\";Port=5432", "Host=db;Password=\"***\";Port=5432")]
    [InlineData("Password: s3cret-pw", "Password: ***")]
    [InlineData("Pool=true;Password=my s3cret pw;Port=5432", "Pool=true;Password=***;Port=5432")]
    public void Mask_ConnectionStringPassword_MasksValueAndKeepsKeyAndNeighbours(string input, string expected)
    {
        SensitiveTextMasker.Mask(input).Should().Be(expected);
    }

    // 對應 OBS-EXMASK-006
    [Theory]
    [InlineData("Authorization: Bearer abc.def.ghi", "abc.def.ghi")]
    [InlineData("token was eyJhbGciOi.eyJzdWIiOi.c2lnbmF0dXJl here", "eyJhbGciOi.eyJzdWIiOi.c2lnbmF0dXJl")]
    public void Mask_BearerAndJwt_RemovesTokenText(string input, string token)
    {
        SensitiveTextMasker.Mask(input).Should().NotContain(token);
    }

    // 對應 OBS-EXMASK-007
    [Fact]
    public void Mask_UrlEmbeddedPassword_ReplacesOnlyPasswordSegment()
    {
        SensitiveTextMasker.Mask("failed redis://user:s3cret-pw@cache:6379 retry")
            .Should().Be("failed redis://user:***@cache:6379 retry");
    }

    [Fact]
    public void Mask_UrlWithPortButNoCredentials_IsUnchanged()
    {
        const string text = "GET https://example.com:8080/path failed";
        SensitiveTextMasker.Mask(text).Should().Be(text);
    }

    // 對應 OBS-EXMASK-008、018
    [Theory]
    [InlineData("apikey=k-123", "k-123")]
    [InlineData("SECRET: k-456", "k-456")]
    [InlineData("Token=k-789", "k-789")]
    [InlineData("{\"password\":\"s3cret-pw\"}", "s3cret-pw")]
    [InlineData("access_token=s3cret-pw", "s3cret-pw")]
    [InlineData("\"client_secret\": \"s3cret-pw\"", "s3cret-pw")]
    [InlineData("X-Api-Key: s3cret-pw", "s3cret-pw")]
    public void Mask_KeyValueSecrets_RemovesValueCaseInsensitively(string input, string plaintext)
    {
        SensitiveTextMasker.Mask(input).Should().NotContain(plaintext);
    }

    // 對應 OBS-EXMASK-026：跳脫引號不得被當成值的結尾，否則密碼尾段會留在輸出中。
    [Theory]
    [InlineData("{\"password\":\"pre\\\"s3cret-pw\"}", "{\"password\":\"***\"}")]
    [InlineData("Host=db;Password='pre\\'s3cret-pw';Port=5432", "Host=db;Password='***';Port=5432")]
    [InlineData("{\"client_secret\":\"a\\\\\\\"s3cret-pw\",\"user\":\"bob\"}", "{\"client_secret\":\"***\",\"user\":\"bob\"}")]
    public void Mask_QuotedValueContainingEscapedQuote_MasksWholeValue(string input, string expected)
    {
        var masked = SensitiveTextMasker.Mask(input);

        masked.Should().NotContain("s3cret-pw").And.Be(expected);
    }

    [Fact]
    public void Mask_JsonPassword_KeepsJsonShape()
    {
        SensitiveTextMasker.Mask("{\"password\":\"s3cret-pw\",\"user\":\"bob\"}")
            .Should().Be("{\"password\":\"***\",\"user\":\"bob\"}");
    }

    // 對應 OBS-EXMASK-028：引號未閉合（例如文字被截斷）時，從開引號到行尾整段遮蔽（fail-closed）。
    [Theory]
    [InlineData("client_secret=\"pre\\\"s3cret-pw", "client_secret=***")]
    [InlineData("token='s3cret-pw", "token=***")]
    [InlineData("ApiKey: \"s3cret-pw\nnext line", "ApiKey: ***\nnext line")]
    public void Mask_UnterminatedQuotedSecret_MasksToEndOfLine(string input, string expected)
    {
        SensitiveTextMasker.Mask(input).Should().Be(expected);
    }

    // 對應 OBS-EXMASK-027：key 內含關鍵字即遮蔽是刻意取捨（寧可多遮蔽也不外洩）；若改成只比對完整 key，此測試應失敗。
    [Theory]
    [InlineData("NotASecret=visible", "NotASecret=***")]
    [InlineData("DatabaseToken: abc123", "DatabaseToken: ***")]
    public void Mask_KeyContainingSensitiveKeyword_MasksValue(string input, string expected)
    {
        SensitiveTextMasker.Mask(input).Should().Be(expected);
    }

    // 對應 OBS-EXMASK-009
    [Theory]
    [InlineData("Sequence contains no elements")]
    [InlineData("at Foo.Bar(CancellationToken cancellationToken) in /src/Foo.cs:line 12")]
    public void Mask_TextWithoutSensitiveFormat_IsUnchanged(string text)
    {
        SensitiveTextMasker.Mask(text).Should().Be(text);
    }

    [Fact]
    public void Mask_FullWidthTextWithinLimit_IsNotTruncated()
    {
        var text = new string('字', SensitiveTextMasker.MaxLength);

        SensitiveTextMasker.Mask(text).Should().Be(text);
    }

    [Fact]
    public void Mask_TextOneCharOverLimit_IsTruncatedWithMarker()
    {
        var text = new string('x', SensitiveTextMasker.MaxLength + 1);

        SensitiveTextMasker.Mask(text).Should().Contain(SensitiveTextMasker.TruncationMarker);
    }

    [Fact]
    public void Mask_NullText_Throws()
    {
        var act = () => SensitiveTextMasker.Mask(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    // 對應 OBS-EXMASK-020
    [Fact]
    public void Mask_SecretInOmittedMiddleOfOversizedText_IsNotOutputAndMarkerPresent()
    {
        var text = Filler(36000) + " Password=" + Secret + " " + Filler(60000) + "\n   at Method()";

        var masked = SensitiveTextMasker.Mask(text);

        masked.Should().NotContain(Secret).And.Contain(SensitiveTextMasker.TruncationMarker);
    }

    // 對應 OBS-EXMASK-021
    [Fact]
    public void Mask_JwtStraddlingHeadLimit_OutputsNoFragmentOfJwt()
    {
        const string jwt = "eyJQ7Z9kQ7Z9kQ7Z9kQ7Z9kQ7Z9kQ7Z9k.Q7Z9kQ7Z9kQ7Z9kQ7Z9kQ7Z9kQ7Z9k.Q7Z9kQ7Z9kQ7Z9kQ7Z9kQ7Z9kQ7Z9k";
        var prefix = FillerEndingWithSpace(SensitiveTextMasker.HeadLength - 50);
        var text = prefix + jwt + " " + Filler(70000);

        var masked = SensitiveTextMasker.Mask(text);

        masked.Should().Contain(SensitiveTextMasker.TruncationMarker);
        AssertNoFragment(masked, jwt, 10);
    }

    // 對應 OBS-EXMASK-022
    [Fact]
    public void Mask_SpacedPasswordCutByHeadLimit_OutputsNoPasswordWordsAfterKey()
    {
        var prefix = FillerEndingWithSpace(SensitiveTextMasker.HeadLength - "Password=my s3".Length);
        var text = prefix + "Password=my s3cret pw;Port=1 " + Filler(70000);

        var masked = SensitiveTextMasker.Mask(text);

        masked.Should().Contain(SensitiveTextMasker.TruncationMarker)
            .And.NotContain("Password=my").And.NotContain("Password=s3cret").And.NotContain("Password=pw")
            .And.NotContain("s3cret");
    }

    // 對應 OBS-EXMASK-024
    [Fact]
    public void Mask_UrlPasswordWithCommaCutByHeadLimit_OutputsNoPasswordFragment()
    {
        var prefix = FillerEndingWithSpace(SensitiveTextMasker.HeadLength - "redis://user:abc,de".Length);
        var text = prefix + "redis://user:abc,def@cache:6379 " + Filler(70000);

        var masked = SensitiveTextMasker.Mask(text);

        masked.Should().Contain(SensitiveTextMasker.TruncationMarker)
            .And.NotContain("redis://user:abc").And.NotContain("abc,de");
    }

    [Fact]
    public void Mask_SecretStraddlingTailStart_OutputsNoSecretFragment()
    {
        const string secretText = "Password=s3cret-pw";
        var text = Filler(40000) + secretText + Filler(SensitiveTextMasker.TailLength - 5 - 20) + "\n   at Method()\n";
        var tailStart = text.Length - SensitiveTextMasker.TailLength;
        var secretStart = text.IndexOf(secretText, StringComparison.Ordinal);
        tailStart.Should().BeInRange(secretStart + 9, secretStart + secretText.Length - 1, "測試前提：尾段起點必須切在密碼內");

        var masked = SensitiveTextMasker.Mask(text);

        masked.Should().NotContain("cret-pw");
    }

    // 對應 OBS-EXMASK-023
    [Fact]
    public void Mask_OversizedMessageKeepsTypeNameAndStackFrames()
    {
        var text = "System.InvalidOperationException: " + Filler(40000) + " Password=" + Secret + " " + Filler(40000)
            + "\n   at Ns.Class.Method() in File.cs:line 1\n   at Other.Call()";

        var masked = SensitiveTextMasker.Mask(text);

        masked.Should().Contain("System.InvalidOperationException")
            .And.Contain("   at Ns.Class.Method()")
            .And.Contain(SensitiveTextMasker.TruncationMarker)
            .And.NotContain(Secret);
    }

    [Fact]
    public void Mask_OversizedText_HeadBacksOffToLastNewlineBeforeLimit()
    {
        var text = "keep-line\n" + Filler(SensitiveTextMasker.MaxLength);

        SensitiveTextMasker.Mask(text).Should().StartWith("keep-line" + SensitiveTextMasker.TruncationMarker);
    }

    [Fact]
    public void Mask_OversizedTextWithoutNewline_HeadBacksOffToLastSpaceAndTailIsEmpty()
    {
        var text = Filler(SensitiveTextMasker.MaxLength + 500);
        var lastSpace = text[..SensitiveTextMasker.HeadLength].LastIndexOf(' ');

        var masked = SensitiveTextMasker.Mask(text);

        masked.Should().Be(text[..lastSpace] + SensitiveTextMasker.TruncationMarker);
    }

    [Fact]
    public void Mask_OversizedTextWithoutWhitespace_HeadAndTailAreEmpty()
    {
        var text = new string('x', SensitiveTextMasker.MaxLength + 500);

        SensitiveTextMasker.Mask(text).Should().Be(SensitiveTextMasker.TruncationMarker);
    }

    [Fact]
    public void Mask_OversizedText_TailStartsAfterFirstNewlineWithinTailWindow()
    {
        var text = Filler(SensitiveTextMasker.MaxLength) + "\nTAIL-START rest";

        SensitiveTextMasker.Mask(text).Should().EndWith(SensitiveTextMasker.TruncationMarker + "TAIL-START rest");
    }

    [Fact]
    public void Patterns_EachHasBoundedMatchTimeout()
    {
        SensitiveTextMasker.Patterns.Should().NotBeEmpty()
            .And.OnlyContain(pattern => pattern.MatchTimeout != Regex.InfiniteMatchTimeout
                && pattern.MatchTimeout <= TimeSpan.FromMilliseconds(100));
    }

    public static TheoryData<string> PathologicalInputs() => new()
    {
        new string('a', SensitiveTextMasker.MaxLength),
        string.Concat(Enumerable.Repeat("eyJ", SensitiveTextMasker.MaxLength / 3)),
        "password" + new string(' ', SensitiveTextMasker.MaxLength - 8),
        "token=" + new string(' ', SensitiveTextMasker.MaxLength - 6),
        "Bearer" + new string(' ', SensitiveTextMasker.MaxLength - 6),
        "a://" + new string('a', SensitiveTextMasker.MaxLength - 4),
        string.Concat(Enumerable.Repeat("password=", SensitiveTextMasker.MaxLength / 9)),
        string.Concat(Enumerable.Repeat("a:", SensitiveTextMasker.MaxLength / 2)),
        string.Concat(Enumerable.Repeat("secret\"", SensitiveTextMasker.MaxLength / 7)),
    };

    [Theory]
    [MemberData(nameof(PathologicalInputs))]
    public void Mask_PathologicalInput_ReturnsOrTimesOutWithinBound(string input)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            SensitiveTextMasker.Mask(input);
        }
        catch (RegexMatchTimeoutException)
        {
            // 逾時屬允許結果；呼叫端（ExceptionMaskingSink）會降級為占位文字。
        }

        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3));
    }

    private static string Filler(int length)
    {
        const string word = "lorem ipsum dolor sit amet ";
        var builder = new StringBuilder(length + word.Length);
        while (builder.Length < length)
        {
            builder.Append(word);
        }

        return builder.ToString(0, length);
    }

    private static string FillerEndingWithSpace(int length)
    {
        return Filler(length - 1) + " ";
    }

    private static void AssertNoFragment(string output, string source, int fragmentLength)
    {
        for (var index = 0; index + fragmentLength <= source.Length; index++)
        {
            output.Should().NotContain(source.Substring(index, fragmentLength));
        }
    }
}
