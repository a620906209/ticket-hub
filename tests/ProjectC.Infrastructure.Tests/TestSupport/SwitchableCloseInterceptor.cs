using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ProjectC.Infrastructure.Tests.TestSupport;

/// <summary>
/// <see cref="IsFailing"/> 為 true 時關閉連線拋出指定例外；可在測試中途切換，先讓一次關閉失敗留下開啟的連線，
/// 再恢復正常或維持失敗。一律失敗的情境用 <see cref="ThrowingCloseInterceptor"/>。
/// </summary>
public sealed class SwitchableCloseInterceptor(Exception failure) : DbConnectionInterceptor
{
    public bool IsFailing { get; set; } = true;

    public override ValueTask<InterceptionResult> ConnectionClosingAsync(
        DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
        => IsFailing ? throw failure : base.ConnectionClosingAsync(connection, eventData, result);
}
