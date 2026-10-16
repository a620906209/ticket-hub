namespace ProjectC.WebApi.Common;

/// <summary>
/// 請求本文大小上限（bytes）。用常數而非 IOptions：<c>[RequestSizeLimit]</c> 參數須為編譯期常數，
/// 且上限是安全預設、不是部署可調參數（request-body-size-limits design.md 決策 1）。
/// </summary>
public static class RequestBodyLimits
{
    /// <summary>未另行指定的所有端點適用的全域上限（1MB）。</summary>
    public const long GlobalBytes = 1_048_576;

    /// <summary>建立訂單端點的上限（16KB），比全域更嚴格。</summary>
    public const long PlaceOrderBytes = 16_384;

    /// <summary>建立座位圖端點的上限（10MB），放寬以容納 50,000 席最長欄位的座位圖。</summary>
    public const long CreateSeatMapBytes = 10_485_760;
}
