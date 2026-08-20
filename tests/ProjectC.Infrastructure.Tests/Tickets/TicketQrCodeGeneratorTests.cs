using FluentAssertions;
using ProjectC.Infrastructure.Tickets;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using ZXing;
using ZXing.Common;

namespace ProjectC.Infrastructure.Tests.Tickets;

public class TicketQrCodeGeneratorTests
{
    [Fact]
    public void GeneratePng_WhenCalledWithTicketId_ReturnsPngThatDecodesToContentVerifyingBackToSameTicketId()
    {
        var signingService = new HmacTicketSigningService(
            new TicketSigningOptions { SigningKey = "unit-test-ticket-signing-key-not-for-prod-32+" });
        var generator = new TicketQrCodeGenerator(signingService);
        var ticketId = Guid.NewGuid();

        var pngBytes = generator.GeneratePng(ticketId);

        pngBytes.Should().NotBeEmpty();

        var decodedContent = DecodeQrContent(pngBytes);
        var verified = signingService.TryVerify(decodedContent, out var restoredTicketId);

        verified.Should().BeTrue();
        restoredTicketId.Should().Be(ticketId);
    }

    // 真正解碼 PNG 像素還原 QR 內容——不像先前只獨立重算 Sign(ticketId) 比對，那種做法就算
    // GeneratePng 內部邏輯壞掉（例如編碼了錯誤或寫死的內容）測試仍會通過。
    private static string DecodeQrContent(byte[] pngBytes)
    {
        using var image = Image.Load<Rgba32>(pngBytes);
        var rgbBytes = new byte[image.Width * image.Height * 3];
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    var offset = (y * image.Width + x) * 3;
                    rgbBytes[offset] = row[x].R;
                    rgbBytes[offset + 1] = row[x].G;
                    rgbBytes[offset + 2] = row[x].B;
                }
            }
        });

        var luminanceSource = new RGBLuminanceSource(rgbBytes, image.Width, image.Height, RGBLuminanceSource.BitmapFormat.RGB24);
        var reader = new BarcodeReaderGeneric
        {
            Options = new DecodingOptions { PossibleFormats = [BarcodeFormat.QR_CODE], TryHarder = true },
        };

        var result = reader.Decode(luminanceSource);
        result.Should().NotBeNull("QRCoder 產出的 PNG 應該要能被標準 QR 解碼器讀回內容");
        return result!.Text;
    }
}
