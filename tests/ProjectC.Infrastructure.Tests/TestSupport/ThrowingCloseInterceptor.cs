using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ProjectC.Infrastructure.Tests.TestSupport;

/// <summary>關閉連線時拋出指定例外，供測試 dispose 路徑遇到關閉失敗時的處理。</summary>
public sealed class ThrowingCloseInterceptor(Exception failure) : DbConnectionInterceptor
{
    public override ValueTask<InterceptionResult> ConnectionClosingAsync(
        DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
        => throw failure;
}
