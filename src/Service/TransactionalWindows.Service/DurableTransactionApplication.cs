using TransactionalWindows.Core.Domain;
using TransactionalWindows.Core.Errors;
using TransactionalWindows.Service.Persistence;

namespace TransactionalWindows.Service;

/// <summary>
/// Durable application boundary for trusted in-process callers. Owner SID must
/// later come from authenticated IPC, never from an untrusted message alone.
/// Lifecycle methods record facts supplied by orchestration; they do not execute apps.
/// </summary>
public sealed class DurableTransactionApplication(DurableTransactionStore store)
{
    public DurableCommandResult Create(Guid operationId, string ownerSid, string applicationPath,
        string arguments, string workingDirectory, string overlayRoot, DateTimeOffset at)
        => store.Create(ownerSid, applicationPath, arguments, workingDirectory, overlayRoot, operationId, at);

    public Transaction Get(TransactionId id, string ownerSid)
    {
        var transaction = store.Get(id) ?? throw new DomainException(new(
            DomainErrorCode.TransactionNotFound, "Transaction was not found."));
        if (!string.Equals(transaction.OwnerSid, ownerSid, StringComparison.Ordinal))
            throw new DomainException(new(DomainErrorCode.SecurityViolation, "Transaction owner does not match."));
        return transaction;
    }

    public Diff? GetDiff(TransactionId id, string ownerSid)
    {
        Get(id, ownerSid);
        return store.GetDiff(id);
    }

    public DurableCommandResult RecordState(TransactionId id, string ownerSid, long expectedVersion,
        TransactionState state, Guid operationId, DateTimeOffset at, string? error = null)
        => store.RecordTransition(id, ownerSid, expectedVersion, state, operationId, at, error);

    public DurableCommandResult RecordDiff(TransactionId id, string ownerSid, long expectedVersion,
        Diff diff, Guid operationId, DateTimeOffset at)
        => store.RecordDiff(id, ownerSid, expectedVersion, diff, operationId, at);

    public IReadOnlyList<StoredEvent> ReadEvents(string ownerSid, long afterSequence)
    {
        var owned = store.List(includeArchived: true).Where(t => t.OwnerSid == ownerSid).Select(t => t.Id).ToHashSet();
        return store.ReplayEvents(afterSequence).Where(e => owned.Contains(e.TransactionId)).ToArray();
    }

    /// <summary>Call once at startup before accepting lifecycle facts from any producer.</summary>
    public IReadOnlyList<RestartEntry> RecoverAfterRestart(DateTimeOffset at) => store.ScanAfterRestart(at);
}
