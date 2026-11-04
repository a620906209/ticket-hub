namespace ProjectC.WebApi.Logging;

/// <summary>
/// 承載遮蔽後的例外文字。Console、Seq 等 sink 都以 <c>ToString()</c> 輸出例外，所以輸出內容就是遮蔽後文字。
/// 不持有原例外的參考，也不帶內部例外，避免任何 sink 繞過遮蔽取到原文（exception-log-masking design.md 決策 2）。
/// </summary>
public sealed class MaskedException : Exception
{
    private readonly string _maskedText;

    public MaskedException(string originalTypeName, string maskedText)
        : base(maskedText)
    {
        OriginalTypeName = originalTypeName;
        _maskedText = maskedText;
    }

    public string OriginalTypeName { get; }

    public override string ToString() => _maskedText;
}
