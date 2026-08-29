namespace ProjectC.Domain.PurchaseQueue;

public sealed class PurchaseQueueEntry
{
    public Guid Id { get; }
    public Guid EventId { get; }
    public Guid MemberId { get; }
    public PurchaseQueueEntryStatus Status { get; private set; }
    public DateTime JoinedAtUtc { get; }
    public DateTime? AdmittedAtUtc { get; private set; }
    public DateTime? AdmissionExpiresAtUtc { get; private set; }

    // 加入排隊用：初始狀態固定為 Waiting，比照 Ticket 既有模式，公開建構子只接受新建立時需要的欄位。
    public PurchaseQueueEntry(Guid id, Guid eventId, Guid memberId, DateTime joinedAtUtc)
    {
        if (eventId == Guid.Empty)
            throw new ArgumentException("Event is required.", nameof(eventId));
        if (memberId == Guid.Empty)
            throw new ArgumentException("Member is required.", nameof(memberId));

        Id = id;
        EventId = eventId;
        MemberId = memberId;
        Status = PurchaseQueueEntryStatus.Waiting;
        JoinedAtUtc = joinedAtUtc;
    }

    // 僅供 EF Core 物化使用：直接還原歷史狀態，不重新驗證（比照 Ticket/Order 既有模式）。
    private PurchaseQueueEntry(
        Guid id,
        Guid eventId,
        Guid memberId,
        PurchaseQueueEntryStatus status,
        DateTime joinedAtUtc,
        DateTime? admittedAtUtc,
        DateTime? admissionExpiresAtUtc)
    {
        Id = id;
        EventId = eventId;
        MemberId = memberId;
        Status = status;
        JoinedAtUtc = joinedAtUtc;
        AdmittedAtUtc = admittedAtUtc;
        AdmissionExpiresAtUtc = admissionExpiresAtUtc;
    }

    public void Admit(DateTime now, DateTime admissionExpiresAtUtc)
    {
        if (Status != PurchaseQueueEntryStatus.Waiting)
            throw new PurchaseQueueEntryNotWaitingException(Id, Status);
        if (admissionExpiresAtUtc <= now)
            throw new ArgumentException("Admission expiry must be after now.", nameof(admissionExpiresAtUtc));

        Status = PurchaseQueueEntryStatus.Admitted;
        AdmittedAtUtc = now;
        AdmissionExpiresAtUtc = admissionExpiresAtUtc;
    }

    public void Complete()
    {
        if (Status != PurchaseQueueEntryStatus.Admitted)
            throw new PurchaseQueueEntryNotAdmittedException(Id, Status);

        Status = PurchaseQueueEntryStatus.Completed;
    }

    public void Expire()
    {
        if (Status != PurchaseQueueEntryStatus.Admitted)
            throw new PurchaseQueueEntryNotAdmittedException(Id, Status);

        Status = PurchaseQueueEntryStatus.Expired;
    }
}
