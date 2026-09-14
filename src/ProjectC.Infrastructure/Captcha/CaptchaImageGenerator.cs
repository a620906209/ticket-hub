using System.Security.Cryptography;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace ProjectC.Infrastructure.Captcha;

// MUST NOT 使用 SystemFonts——容器 base image 不內建系統字型，SystemFonts 在容器內會拋出
// FontException（captcha-verification design.md 決策 1）。字型以 embedded resource 內嵌，隨 Scoped
// 生命週期由每個執行個體各自解析一次（design.md 決策 13：避免與未經證實 thread-safe 的
// FontCollection／FontFamily 跨執行緒共用）。
public sealed class CaptchaImageGenerator : ICaptchaImageGenerator
{
    // 字元集排除 0/O、1/I/L 等易混淆字元（captcha-verification design.md 決策 2）。
    // 驗證碼答案是安全敏感內容，必須使用密碼學安全亂數，不得使用 Random.Shared。
    private const string AllowedCharacters = "23456789ABCDEFGHJKMNPQRSTUVWXYZ";
    private const int ContentLength = 4;
    private const int ImageWidth = 160;
    private const int ImageHeight = 60;
    private const int NoiseLineCount = 6;

    private readonly Font _font;

    public CaptchaImageGenerator()
    {
        var assembly = typeof(CaptchaImageGenerator).Assembly;
        using var fontStream = assembly.GetManifestResourceStream("ProjectC.Infrastructure.Captcha.Fonts.DejaVuSans.ttf")
            ?? throw new InvalidOperationException("Embedded captcha font resource 'DejaVuSans.ttf' was not found.");

        var fontCollection = new FontCollection();
        var fontFamily = fontCollection.Add(fontStream);
        _font = fontFamily.CreateFont(32, FontStyle.Bold);
    }

    public CaptchaImage Generate()
    {
        var content = GenerateContent();
        var imageBytes = DrawImage(content);
        return new CaptchaImage(content, imageBytes);
    }

    private static string GenerateContent()
    {
        Span<char> buffer = stackalloc char[ContentLength];
        for (var i = 0; i < ContentLength; i++)
        {
            buffer[i] = AllowedCharacters[RandomNumberGenerator.GetInt32(AllowedCharacters.Length)];
        }

        return new string(buffer);
    }

    private byte[] DrawImage(string content)
    {
        using var image = new Image<Rgba32>(ImageWidth, ImageHeight, Color.White);

        image.Mutate(ctx =>
        {
            for (var i = 0; i < NoiseLineCount; i++)
            {
                var start = new PointF(Random.Shared.Next(ImageWidth), Random.Shared.Next(ImageHeight));
                var end = new PointF(Random.Shared.Next(ImageWidth), Random.Shared.Next(ImageHeight));
                ctx.DrawLine(Color.LightGray, 1f, start, end);
            }

            var textOptions = new RichTextOptions(_font)
            {
                Origin = new PointF(ImageWidth / 2f, ImageHeight / 2f),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };

            ctx.DrawText(textOptions, content, Color.Black);
        });

        using var memoryStream = new MemoryStream();
        image.SaveAsPng(memoryStream);
        return memoryStream.ToArray();
    }
}
