using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ProjectC.LoadTest.Seeder;

/// <summary>
/// 寫出含有效 JWT 的 token 檔，只讓 k6 容器的使用者讀取（k6-load-test design.md 決策 10「檔案權限」）。
/// seeder 在 api 容器內以 root 執行，root 不受檔案權限限制，所以把擁有者交給 k6 後仍能覆寫。
/// </summary>
internal static class TokenFileWriter
{
    // grafana/k6:2.3.0 image 的使用者（容器內 `id` 實測 uid=12345 gid=12345，2026-10-05）；升級 k6 image 時須重新確認。
    internal const int K6UserId = 12345;
    internal const int K6GroupId = 12345;

    // 目錄只給 k6：k6 要在這裡寫 summary JSON，其他使用者不得列出或讀取。
    internal const UnixFileMode OutputDirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    internal const UnixFileMode TokenFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public static async Task WriteAsync(string tokenFilePath, string content, CancellationToken cancellationToken)
    {
        // .output 被 gitignore，新 clone 時不存在。
        var outputDirectory = Directory.CreateDirectory(Path.GetDirectoryName(tokenFilePath)!);
        if (OperatingSystem.IsWindows())
        {
            await File.WriteAllTextAsync(tokenFilePath, content, cancellationToken);
            return;
        }

        await WriteRestrictedAsync(outputDirectory, tokenFilePath, content, cancellationToken);
    }

    [UnsupportedOSPlatform("windows")]
    private static async Task WriteRestrictedAsync(DirectoryInfo outputDirectory, string tokenFilePath, string content, CancellationToken cancellationToken)
    {
        // 每次都設：目錄可能是先前版本以 0777 或 root 0755 建立的。
        outputDirectory.UnixFileMode = OutputDirectoryMode;
        ChangeOwnerToK6(outputDirectory.FullName);

        // 先寫暫存檔再改名：既有 tokens.json 可能是舊版留下的 0644，直接覆寫會沿用舊權限；
        // 建立當下就是 0600，不留任何可被他人讀取的空窗。
        var temporaryPath = tokenFilePath + ".tmp";
        File.Delete(temporaryPath);
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                UnixCreateMode = TokenFileMode,
            };
            await using (var writer = new StreamWriter(new FileStream(temporaryPath, options)))
                await writer.WriteAsync(content.AsMemory(), cancellationToken);

            ChangeOwnerToK6(temporaryPath);
            File.Move(temporaryPath, tokenFilePath, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    // .NET 沒有變更擁有者的 managed API。Unix 上 CharSet.Ansi 即 UTF-8。
    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern int chown(string path, int owner, int group);

    private static void ChangeOwnerToK6(string path)
    {
        if (chown(path, K6UserId, K6GroupId) != 0)
            throw new IOException($"chown {path} to {K6UserId}:{K6GroupId} failed (errno {Marshal.GetLastPInvokeError()}); the seeder must run as root inside the api container.");
    }
}
