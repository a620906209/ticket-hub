namespace ProjectC.Application.PurchaseQueue.GetMyQueueStatus;

// Status："NotJoined" / "Waiting" / "Admitted" / "Expired"；WaitingCount 僅 Waiting 時有值，其餘為 null；
// QueueModeEnabled 反映查詢當下該活動的 Event.IsQueueModeEnabled（design.md 決策 6）。
public sealed record QueueStatusDto(string Status, int? WaitingCount, bool QueueModeEnabled);
