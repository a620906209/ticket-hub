using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using ProjectC.WebApi.Common;

namespace ProjectC.WebApi.Extensions;

public static class ForwardedHeadersServiceCollectionExtensions
{
    // 只還原 X-Forwarded-For／X-Forwarded-Proto 一層，且僅信任 ForwardedHeaders:TrustedNetworks 內的來源
    // （reverse-proxy-forwarded-headers design.md 決策 1）。以 IConfiguration 注入的 Configure 延遲到選項被解析時
    // 才讀設定，確保取得的是最終合併後的設定；非法設定由 ResolveEagerlyValidatedOptions 於啟動時觸發而失敗。
    public static IServiceCollection AddClientAddressResolution(this IServiceCollection services)
    {
        services
            .AddOptions<ForwardedHeadersOptions>()
            .Configure<IConfiguration>((options, configuration) =>
            {
                var settings = ForwardedHeadersSettings.FromConfiguration(configuration);

                options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
                options.ForwardLimit = 1;

                // 框架預設只信任 loopback；先清空，避免預設值與設定檔混合。
                options.KnownProxies.Clear();
                options.KnownIPNetworks.Clear();
                foreach (var network in settings.TrustedNetworks)
                {
                    options.KnownIPNetworks.Add(network);
                }
            });

        return services;
    }

    public static void ResolveForwardedHeadersOptions(this IServiceProvider services)
        => _ = services.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;
}
