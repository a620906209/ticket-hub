using FluentAssertions;
using ProjectC.Domain.PurchaseQueue;

namespace ProjectC.Domain.Tests.PurchaseQueue;

public class PurchaseQueueEntryTests
{
    private static readonly DateTime JoinedAtUtc = new(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);

    private static PurchaseQueueEntry CreateWaitingEntry()
        => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), JoinedAtUtc);

    [Fact]
    public void Constructor_WhenCreated_HasWaitingStatusAndNoAdmissionFields()
    {
        var entry = CreateWaitingEntry();

        entry.Status.Should().Be(PurchaseQueueEntryStatus.Waiting);
        entry.JoinedAtUtc.Should().Be(JoinedAtUtc);
        entry.AdmittedAtUtc.Should().BeNull();
        entry.AdmissionExpiresAtUtc.Should().BeNull();
    }

    [Fact]
    public void Constructor_WhenEventIdIsEmpty_ThrowsArgumentException()
    {
        var act = () => new PurchaseQueueEntry(Guid.NewGuid(), Guid.Empty, Guid.NewGuid(), JoinedAtUtc);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_WhenMemberIdIsEmpty_ThrowsArgumentException()
    {
        var act = () => new PurchaseQueueEntry(Guid.NewGuid(), Guid.NewGuid(), Guid.Empty, JoinedAtUtc);

        act.Should().Throw<ArgumentException>();
    }

    // ---- Admit ----

    [Fact]
    public void Admit_WhenWaiting_TransitionsToAdmittedAndRecordsTimestamps()
    {
        var entry = CreateWaitingEntry();
        var now = JoinedAtUtc.AddMinutes(5);
        var expiresAt = now.AddMinutes(5);

        entry.Admit(now, expiresAt);

        entry.Status.Should().Be(PurchaseQueueEntryStatus.Admitted);
        entry.AdmittedAtUtc.Should().Be(now);
        entry.AdmissionExpiresAtUtc.Should().Be(expiresAt);
    }

    [Fact]
    public void Admit_WhenAlreadyAdmitted_ThrowsPurchaseQueueEntryNotWaitingException()
    {
        var entry = CreateWaitingEntry();
        var now = JoinedAtUtc.AddMinutes(5);
        entry.Admit(now, now.AddMinutes(5));

        var act = () => entry.Admit(now.AddMinutes(1), now.AddMinutes(6));

        act.Should().Throw<PurchaseQueueEntryNotWaitingException>();
        entry.Status.Should().Be(PurchaseQueueEntryStatus.Admitted);
    }

    [Fact]
    public void Admit_WhenExpiresAtIsNotAfterNow_ThrowsArgumentException()
    {
        var entry = CreateWaitingEntry();
        var now = JoinedAtUtc.AddMinutes(5);

        var act = () => entry.Admit(now, now);

        act.Should().Throw<ArgumentException>();
        entry.Status.Should().Be(PurchaseQueueEntryStatus.Waiting, "非法呼叫不應該讓狀態機留在中間狀態");
    }

    // ---- Complete ----

    [Fact]
    public void Complete_WhenAdmitted_TransitionsToCompleted()
    {
        var entry = CreateWaitingEntry();
        var now = JoinedAtUtc.AddMinutes(5);
        entry.Admit(now, now.AddMinutes(5));

        entry.Complete();

        entry.Status.Should().Be(PurchaseQueueEntryStatus.Completed);
    }

    [Fact]
    public void Complete_WhenStillWaiting_ThrowsPurchaseQueueEntryNotAdmittedException()
    {
        var entry = CreateWaitingEntry();

        var act = () => entry.Complete();

        act.Should().Throw<PurchaseQueueEntryNotAdmittedException>();
        entry.Status.Should().Be(PurchaseQueueEntryStatus.Waiting);
    }

    [Fact]
    public void Complete_WhenAlreadyCompleted_ThrowsPurchaseQueueEntryNotAdmittedException()
    {
        var entry = CreateWaitingEntry();
        var now = JoinedAtUtc.AddMinutes(5);
        entry.Admit(now, now.AddMinutes(5));
        entry.Complete();

        var act = () => entry.Complete();

        act.Should().Throw<PurchaseQueueEntryNotAdmittedException>();
    }

    // ---- Expire ----

    [Fact]
    public void Expire_WhenAdmitted_TransitionsToExpired()
    {
        var entry = CreateWaitingEntry();
        var now = JoinedAtUtc.AddMinutes(5);
        entry.Admit(now, now.AddMinutes(5));

        entry.Expire();

        entry.Status.Should().Be(PurchaseQueueEntryStatus.Expired);
    }

    [Fact]
    public void Expire_WhenStillWaiting_ThrowsPurchaseQueueEntryNotAdmittedException()
    {
        var entry = CreateWaitingEntry();

        var act = () => entry.Expire();

        act.Should().Throw<PurchaseQueueEntryNotAdmittedException>();
        entry.Status.Should().Be(PurchaseQueueEntryStatus.Waiting);
    }

    [Fact]
    public void Expire_WhenAlreadyCompleted_ThrowsPurchaseQueueEntryNotAdmittedException()
    {
        var entry = CreateWaitingEntry();
        var now = JoinedAtUtc.AddMinutes(5);
        entry.Admit(now, now.AddMinutes(5));
        entry.Complete();

        var act = () => entry.Expire();

        act.Should().Throw<PurchaseQueueEntryNotAdmittedException>();
    }
}
