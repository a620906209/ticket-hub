using System.Text.Json;

namespace ProjectC.LoadTest.Seeder;

/// <summary>k6 以 <c>open('/output/tokens.json')</c> 讀取的 token 檔格式（k6-load-test design.md 決策 4）。</summary>
public sealed record LoadTestTokenFile(DateTime IssuedAtUtc, string AdminToken, IReadOnlyList<string> BuyerTokens)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public string Serialize() => JsonSerializer.Serialize(this, SerializerOptions);

    public static LoadTestTokenFile Deserialize(string json)
        => JsonSerializer.Deserialize<LoadTestTokenFile>(json, SerializerOptions)
           ?? throw new JsonException("Token file is empty.");
}
