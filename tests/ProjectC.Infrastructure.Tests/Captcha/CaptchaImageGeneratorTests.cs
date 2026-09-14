using System.Runtime.CompilerServices;
using FluentAssertions;
using ProjectC.Infrastructure.Captcha;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace ProjectC.Infrastructure.Tests.Captcha;

public class CaptchaImageGeneratorTests
{
    // 字元集排除 0/O、1/I/L 等易混淆字元（captcha-verification design.md 決策 2）。
    private const string DisallowedCharacters = "0O1Il";

    [Fact]
    public void Generate_ReturnsFourCharacterContentExcludingConfusingCharacters()
    {
        var generator = new CaptchaImageGenerator();

        var result = generator.Generate();

        result.Content.Should().HaveLength(4);
        foreach (var disallowed in DisallowedCharacters)
        {
            result.Content.Should().NotContain(disallowed.ToString());
        }
    }

    [Fact]
    public void Generate_ReturnsNonEmptyPngBytes()
    {
        var generator = new CaptchaImageGenerator();

        var result = generator.Generate();

        result.ImageBytes.Should().NotBeEmpty();
    }

    // 5.4b：容器內字型可用性驗證，MUST 在 docker compose exec api dotnet test 實際容器內執行——不拋出
    // SixLabors.Fonts.FontException，且產出的 PNG 確實含有非純背景色的繪製痕跡，證明文字確實被繪製
    // 出來，而非因字型載入失敗只產出空白圖片（captcha-verification design.md 決策 1）。
    [Fact]
    public void Generate_PngContainsNonBackgroundPixels_ProvingTextWasActuallyDrawn()
    {
        // 建構子本身就會載入內嵌字型；若容器內字型不可用會在這裡拋出 FontException，
        // 讓測試以未預期例外失敗，不需要額外的 Should().NotThrow() 包裝。
        var generator = new CaptchaImageGenerator();

        var result = generator.Generate();

        using var image = Image.Load<Rgba32>(result.ImageBytes);
        var backgroundColor = image[0, 0];
        var hasNonBackgroundPixel = false;
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height && !hasNonBackgroundPixel; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    if (row[x] != backgroundColor)
                    {
                        hasNonBackgroundPixel = true;
                        break;
                    }
                }
            }
        });

        hasNonBackgroundPixel.Should().BeTrue("字型載入失敗時只會產出純背景色的空白圖片");
    }

    // 驗證碼答案是防機器人安全邊界的核心秘密，MUST 使用密碼學安全亂數（RandomNumberGenerator），
    // 不得使用一般用途 PRNG（Random.Shared）——後者是可預測的演算法（.NET 目前為 xoshiro256**），
    // 若答案產生器的內部狀態被推測，會降低猜測驗證碼答案的成本。這條規則只限「答案內容」：
    // 雜訊線的位置不是驗證秘密，繼續使用 Random.Shared 不受此限制，因此這裡直接核對原始碼、
    // 只掃描 GenerateContent 方法本體（不含 DrawImage），避免誤判合法的雜訊線用法。
    // 用 [CallerFilePath] 取得本測試檔案的編譯期絕對路徑，據此推導出同目錄樹下的正式原始碼路徑
    // （tests/ProjectC.Infrastructure.Tests/... → src/ProjectC.Infrastructure/...），不依賴執行時的
    // working directory 假設。
    [Fact]
    public void GenerateContent_MethodBody_DoesNotUseGeneralPurposePseudoRandom()
    {
        var sourceFilePath = GetProductionSourceFilePath();
        File.Exists(sourceFilePath).Should().BeTrue(
            $"預期能在 {sourceFilePath} 找到 CaptchaImageGenerator.cs，測試專案與正式專案的相對目錄結構若改變，這個路徑推導也要跟著更新");

        var source = File.ReadAllText(sourceFilePath);
        var methodBody = ExtractMethodBody(source, "private static string GenerateContent()", "private byte[] DrawImage(");

        methodBody.Should().NotContain("Random.Shared",
            "驗證碼答案內容 MUST 使用 RandomNumberGenerator（密碼學安全亂數），不得使用一般用途 PRNG");
        methodBody.Should().Contain("RandomNumberGenerator.GetInt32",
            "確認測試本身有正確找到並掃描到 GenerateContent 方法本體，不是誤掃到空字串或別的方法");
    }

    private static string ExtractMethodBody(string source, string startMarker, string endMarker)
    {
        var startIndex = source.IndexOf(startMarker, StringComparison.Ordinal);
        startIndex.Should().BeGreaterThan(-1, $"應該能在原始碼中找到 '{startMarker}'");
        var endIndex = source.IndexOf(endMarker, startIndex, StringComparison.Ordinal);
        endIndex.Should().BeGreaterThan(startIndex, $"應該能在 '{startMarker}' 之後找到 '{endMarker}'");
        return source[startIndex..endIndex];
    }

    private static string GetProductionSourceFilePath([CallerFilePath] string testFilePath = "")
    {
        // testFilePath 範例：/src/tests/ProjectC.Infrastructure.Tests/Captcha/CaptchaImageGeneratorTests.cs
        // 往上兩層到 tests/ProjectC.Infrastructure.Tests，再往上一層到 tests，平行切換到 src。
        var testProjectDir = Path.GetDirectoryName(Path.GetDirectoryName(testFilePath))!;
        var testsRootDir = Path.GetDirectoryName(testProjectDir)!;
        var srcRootDir = Path.Combine(Path.GetDirectoryName(testsRootDir)!, "src");
        return Path.GetFullPath(Path.Combine(srcRootDir, "ProjectC.Infrastructure", "Captcha", "CaptchaImageGenerator.cs"));
    }
}
