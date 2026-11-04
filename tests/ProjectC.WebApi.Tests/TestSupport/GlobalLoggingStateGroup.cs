namespace ProjectC.WebApi.Tests.TestSupport;

// 使用 Console.SetOut、Log.Logger 等 process 全域狀態的測試放進這個 collection。具名 collection
// 只保證 collection 內序列，不保證與其他測試類別互斥，因此必須 DisableParallelization = true
// （exception-log-masking design.md 決策 9）。
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GlobalLoggingStateGroup
{
    public const string Name = "GlobalLoggingState";
}
