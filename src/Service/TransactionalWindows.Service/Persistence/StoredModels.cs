using TransactionalWindows.Core.Domain;
using System.Text.Json.Serialization;

namespace TransactionalWindows.Service.Persistence;

public sealed record StoredOperation(Guid OperationId, string RequestDigest, Transaction Response, long Sequence);

public sealed record StoredEvent(
    long Sequence,
    Guid EventId,
    TransactionId TransactionId,
    Guid OperationId,
    string Name,
    TransactionState? From,
    TransactionState To,
    DateTimeOffset OccurredAt);

public sealed record DurableCommandResult(Transaction Transaction, long Sequence, bool Replayed);

public enum RestartDisposition
{
    InspectCreated,
    ReviewDiff,
    InspectTerminal,
    RecoveryRequired
}

public sealed record RestartEntry(
    TransactionId? TransactionId,
    TransactionState? State,
    RestartDisposition Disposition,
    string? Error);

internal sealed record JournalEnvelope(int SchemaVersion, long Sequence, string Payload, string Sha256);

internal sealed record JournalMutation(
    Transaction Transaction,
    Diff? Diff,
    bool Archived,
    StoredOperation Operation,
    StoredEvent Event,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ProcessNode? Process = null);
