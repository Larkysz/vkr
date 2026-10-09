using TransactionalWindows.Core.Domain;

namespace TransactionalWindows.Core.Contracts;

public interface ITransactionManager
{
    Transaction Create(string ownerSid, string applicationPath, string arguments, string workingDirectory, string overlayRoot, DateTimeOffset now);
    Transaction Transition(Transaction transaction, TransactionState target, DateTimeOffset now);
}

public interface ITransactionRepository
{
    Transaction Create(Transaction transaction);
    Transaction? Get(TransactionId id);
    Transaction UpdateState(TransactionId id, TransactionState expected, TransactionState target, DateTimeOffset at);
    void Save(Transaction transaction);
    void Archive(TransactionId id);
}

public interface IProcessManager
{
    Task<IReadOnlyList<ProcessNode>> GetTreeAsync(TransactionId transactionId, CancellationToken cancellationToken);
    Task QuiesceAsync(TransactionId transactionId, TimeSpan timeout, CancellationToken cancellationToken);
}

public interface IOverlayStore
{
    Task CreateTransactionAreaAsync(TransactionId transactionId, CancellationToken cancellationToken);
    Task<IReadOnlyList<FileChange>> ReadFileChangesAsync(TransactionId transactionId, CancellationToken cancellationToken);
    Task<IReadOnlyList<RegistryChange>> ReadRegistryChangesAsync(TransactionId transactionId, CancellationToken cancellationToken);
    Task CleanupAsync(TransactionId transactionId, CancellationToken cancellationToken);
}

public interface IDiffEngine
{
    Task<Diff> BuildAsync(TransactionId transactionId, long generation, CancellationToken cancellationToken);
}

public interface IConflictDetector
{
    Task<IReadOnlyList<DiffItemId>> FindConflictsAsync(Diff diff, CancellationToken cancellationToken);
}

public interface ICommitEngine
{
    Task CommitAsync(Diff diff, IReadOnlySet<DiffItemId> selected, CancellationToken cancellationToken);
    Task DiscardAsync(TransactionId transactionId, CancellationToken cancellationToken);
}

public interface IRecoveryJournal
{
    Task AppendAsync(RecoveryEntry entry, CancellationToken cancellationToken);
    Task<IReadOnlyList<RecoveryEntry>> ReadAsync(TransactionId transactionId, CancellationToken cancellationToken);
}

public interface IEventPublisher
{
    Task PublishAsync(IDomainEvent domainEvent, CancellationToken cancellationToken);
}

