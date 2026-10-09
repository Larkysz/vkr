using TransactionalWindows.Core.Domain;

namespace TransactionalWindows.Core.Contracts;

public interface IDomainEvent
{
    Guid EventId { get; }
    DateTimeOffset OccurredAt { get; }
    TransactionId TransactionId { get; }
}

public abstract record TransactionEvent(TransactionId TransactionId, DateTimeOffset OccurredAt) : IDomainEvent
{
    public Guid EventId { get; init; } = Guid.NewGuid();
}

public sealed record TransactionStateChanged(TransactionId TransactionId, TransactionState From, TransactionState To, DateTimeOffset OccurredAt) : TransactionEvent(TransactionId, OccurredAt);
public sealed record ProcessStarted(TransactionId TransactionId, ProcessNodeId ProcessNodeId, DateTimeOffset OccurredAt) : TransactionEvent(TransactionId, OccurredAt);
public sealed record ProcessExited(TransactionId TransactionId, ProcessNodeId ProcessNodeId, DateTimeOffset OccurredAt) : TransactionEvent(TransactionId, OccurredAt);
public sealed record FileChanged(TransactionId TransactionId, FileChangeId ChangeId, DateTimeOffset OccurredAt) : TransactionEvent(TransactionId, OccurredAt);
public sealed record RegistryChanged(TransactionId TransactionId, RegistryChangeId ChangeId, DateTimeOffset OccurredAt) : TransactionEvent(TransactionId, OccurredAt);
public sealed record DiffReady(TransactionId TransactionId, DiffId DiffId, DateTimeOffset OccurredAt) : TransactionEvent(TransactionId, OccurredAt);
public sealed record ConflictDetected(TransactionId TransactionId, DiffItemId ItemId, DateTimeOffset OccurredAt) : TransactionEvent(TransactionId, OccurredAt);
public sealed record CommitStarted(TransactionId TransactionId, string RequestId, DateTimeOffset OccurredAt) : TransactionEvent(TransactionId, OccurredAt);
public sealed record CommitCompleted(TransactionId TransactionId, string RequestId, DateTimeOffset OccurredAt) : TransactionEvent(TransactionId, OccurredAt);
public sealed record CommitFailed(TransactionId TransactionId, string RequestId, DateTimeOffset OccurredAt) : TransactionEvent(TransactionId, OccurredAt);

