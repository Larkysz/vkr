namespace TransactionalWindows.Core.Domain;

public sealed record Conflict(
    DiffItemId ItemId,
    string ExpectedFingerprint,
    string ObservedFingerprint,
    string Reason,
    DateTimeOffset DetectedAt);

public sealed record CommitPlan(
    string OperationId,
    TransactionId TransactionId,
    DiffId DiffId,
    long DiffGeneration,
    IReadOnlySet<DiffItemId> RequestedItems,
    IReadOnlySet<DiffItemId> RequiredItems,
    IReadOnlyList<DiffItemId> OrderedItems,
    DateTimeOffset CreatedAt);

public sealed record RecoveryEntry(
    string OperationId,
    TransactionId TransactionId,
    string ChangeId,
    string Operation,
    string Target,
    string PreviousState,
    string NewState,
    string Status);

