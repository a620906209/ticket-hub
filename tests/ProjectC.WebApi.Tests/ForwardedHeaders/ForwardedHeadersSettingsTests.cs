using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using ProjectC.WebApi.Common;

namespace ProjectC.WebApi.Tests.ForwardedHeaders;

// client-address-resolution spec CAR-005～007：信任網段的預設值、自訂取代預設、設定錯誤時 fail-fast。
// 防的是兩種後果：預設值缺漏使部署後所有請求都不被還原；非法設定被靜默略過，使信任範圍比管理者以為的更寬或更窄。
public class ForwardedHeadersSettingsTests
{
    private const string ConfigurationKey = "ForwardedHeaders:TrustedNetworks";

    private static IConfiguration BuildConfiguration(params string?[] trustedNetworks)
    {
        var values = new Dictionary<string, string?>();
        for (var index = 0; index < trustedNetworks.Length; index++)
        {
            values[$"{ConfigurationKey}:{index}"] = trustedNetworks[index];
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static IConfiguration BuildJsonConfiguration(string json)
        => new ConfigurationBuilder().AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json))).Build();

    private static bool IsTrusted(ForwardedHeadersSettings settings, string address)
        => settings.TrustedNetworks.Any(network => network.Contains(IPAddress.Parse(address)));

    // CAR-005
    [Theory]
    [InlineData("10.2.3.4")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.254")]
    [InlineData("192.168.1.5")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public void FromConfiguration_WhenKeyMissing_TrustsPrivateNetworksAndLoopback(string address)
    {
        var settings = ForwardedHeadersSettings.FromConfiguration(new ConfigurationBuilder().Build());

        IsTrusted(settings, address).Should().BeTrue();
    }

    [Theory]
    [InlineData("203.0.113.7")]
    [InlineData("172.32.0.1")]
    [InlineData("11.0.0.1")]
    public void FromConfiguration_WhenKeyMissing_DoesNotTrustPublicAddresses(string address)
    {
        var settings = ForwardedHeadersSettings.FromConfiguration(new ConfigurationBuilder().Build());

        IsTrusted(settings, address).Should().BeFalse();
    }

    // CAR-006
    [Fact]
    public void FromConfiguration_WithCustomNetworks_ReplacesDefaults()
    {
        var settings = ForwardedHeadersSettings.FromConfiguration(BuildConfiguration("10.1.0.0/16"));

        IsTrusted(settings, "10.1.2.3").Should().BeTrue();
        IsTrusted(settings, "192.168.1.5").Should().BeFalse("自訂網段取代而非附加於預設");
        IsTrusted(settings, "127.0.0.1").Should().BeFalse();
    }

    // CAR-007
    [Theory]
    [InlineData("not-a-cidr")]
    [InlineData("10.0.0.0/33")]
    [InlineData("10.0.0.0")]
    [InlineData("")]
    [InlineData("   ")]
    public void FromConfiguration_WithInvalidEntry_ThrowsNamingTheConfigurationKey(string invalidEntry)
    {
        var act = () => ForwardedHeadersSettings.FromConfiguration(BuildConfiguration("10.0.0.0/8", invalidEntry));

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{ConfigurationKey}*");
    }

    [Fact]
    public void FromConfiguration_WithInvalidEntry_DoesNotSilentlySkipItAndKeepTheRest()
    {
        var act = () => ForwardedHeadersSettings.FromConfiguration(BuildConfiguration("10.0.0.0/8", "not-a-cidr"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*not-a-cidr*");
    }

    [Fact]
    public void FromConfiguration_WithExplicitEmptyArrayInJson_Throws()
    {
        var act = () => ForwardedHeadersSettings.FromConfiguration(
            BuildJsonConfiguration("""{ "ForwardedHeaders": { "TrustedNetworks": [] } }"""));

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{ConfigurationKey}*");
    }

    [Fact]
    public void FromConfiguration_WithEmptyStringValue_Throws()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [ConfigurationKey] = string.Empty })
            .Build();

        var act = () => ForwardedHeadersSettings.FromConfiguration(configuration);

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{ConfigurationKey}*");
    }
}
