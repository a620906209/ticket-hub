using System.ComponentModel.DataAnnotations;

namespace ProjectC.Application.Common;

// 有安全的預設值（LockTtlMultiplier = 3），不需要 ValidateOnStart；設定但為 0 或負數時
// 仍 MUST 被 DataAnnotations 擋下（purchase-queue-leader-election design.md Migration Plan）。
public sealed class DistributedLockOptions
{
    public const string SectionName = "DistributedLock";

    [Range(1, int.MaxValue)]
    public int LockTtlMultiplier { get; set; } = 3;
}
