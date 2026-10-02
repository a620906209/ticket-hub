namespace ProjectC.Application.Members;

/// <summary>real-name-verification design.md 決策 5：完整末四碼只在查詢持票人端點出現，其他回應一律經此遮蔽。</summary>
public static class RealNameMasking
{
    public static string MaskNationalIdLast4(string nationalIdLast4)
    {
        ArgumentNullException.ThrowIfNull(nationalIdLast4);
        if (nationalIdLast4.Length != 4)
            throw new ArgumentException("National id last 4 must be exactly 4 characters.", nameof(nationalIdLast4));

        return "**" + nationalIdLast4[2..];
    }
}
