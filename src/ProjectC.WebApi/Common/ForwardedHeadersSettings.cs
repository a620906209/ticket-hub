using System.Net;

namespace ProjectC.WebApi.Common;

// 反向代理信任網段的設定解析（reverse-proxy-forwarded-headers design.md 決策 1）。
// 設定缺漏與「明確設定為空」以設定鍵是否存在區分：前者採用預設網段，後者視為誤設定而啟動失敗；
// 任一項非法也一律啟動失敗，不略過該項——靜默略過會讓信任範圍悄悄偏離管理者的意圖。
public sealed class ForwardedHeadersSettings
{
    public const string TrustedNetworksKey = "ForwardedHeaders:TrustedNetworks";

    private static readonly string[] DefaultTrustedNetworks =
    [
        "10.0.0.0/8",
        "172.16.0.0/12",
        "192.168.0.0/16",
        "127.0.0.0/8",
        "::1/128",
    ];

    private ForwardedHeadersSettings(IReadOnlyList<IPNetwork> trustedNetworks)
    {
        TrustedNetworks = trustedNetworks;
    }

    public IReadOnlyList<IPNetwork> TrustedNetworks { get; }

    public static ForwardedHeadersSettings FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection(TrustedNetworksKey);
        if (!section.Exists())
        {
            return new ForwardedHeadersSettings(DefaultTrustedNetworks.Select(ParseNetwork).ToList());
        }

        var entries = section.GetChildren().Select(child => child.Value).ToList();
        if (entries.Count == 0)
        {
            throw new InvalidOperationException(
                $"{TrustedNetworksKey} 已設定但沒有任何項目；若要使用預設網段請移除此設定，若要停用還原請不要信任任何代理網段以外的來源。");
        }

        return new ForwardedHeadersSettings(entries.Select(ParseNetwork).ToList());
    }

    private static IPNetwork ParseNetwork(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || !IPNetwork.TryParse(text.Trim(), out var network))
        {
            throw new InvalidOperationException($"{TrustedNetworksKey} 含有非法的 CIDR 項目：\"{text}\"。");
        }

        return network;
    }
}
